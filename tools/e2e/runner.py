"""Development-only real-video validation using Trackdub's existing CLI."""
import argparse
import asyncio
import hashlib
import json
import math
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
import uuid


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def confined(root, value):
    root = Path(root).resolve()
    path = Path(value)
    path = (root / path).resolve() if not path.is_absolute() else path.resolve()
    if not path.is_relative_to(root):
        raise ValueError(f'Artifact escapes project directory: {value}')
    return path


def terminate_tree(proc):
    if os.name == 'nt':
        result = subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'], capture_output=True, timeout=30, check=False)
        if result.returncode and proc.poll() is None:
            raise RuntimeError('Could not terminate owned process tree')
    else:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    proc.wait(timeout=30)


def execute(argv, cwd, timeout, output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    options = {'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == 'nt' else {'start_new_session': True}
    with (output / 'stdout.log').open('wb') as stdout, (output / 'stderr.log').open('wb') as stderr:
        proc = subprocess.Popen(argv, cwd=cwd, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, **options)
        state = 'exited'
        try:
            proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            state = 'timed_out'
            terminate_tree(proc)
        except KeyboardInterrupt:
            state = 'cancelled'
            terminate_tree(proc)
    return {'command': argv, 'cwd': str(cwd), 'state': state, 'exit_code': proc.returncode, 'elapsed_seconds': time.monotonic() - start}


def build_command(config, media, project):
    voice_clone = config.get('voice_clone', False)
    if not isinstance(voice_clone, bool):
        raise ValueError('voice_clone must be an explicit JSON boolean')
    prefix = config['cli_command']
    if not isinstance(prefix, list) or not prefix or not all(isinstance(x, str) for x in prefix):
        raise ValueError('cli_command must be a nonempty argv array')
    forbidden = {'--media', '--output', '--input-dir', '--input-glob', '--voice-clone', '--force-rerun', '--only', '--from-stage', '--skip-preflight', '--progress'}
    args = config.get('pipeline_args', [])
    if not isinstance(args, list) or not all(isinstance(x, str) for x in args):
        raise ValueError('pipeline_args must be a string array')
    if any(x.split('=', 1)[0] in forbidden for x in args):
        raise ValueError('pipeline_args cannot override inputs, stage scope, preflight, or consent')
    command = prefix + ['--progress', 'json', 'run', 'pipeline', '--media', str(media), '--target-language', config['target_language'], '--output', str(project), '--force-rerun'] + args
    return command + (['--voice-clone'] if voice_clone else [])


def validate_stages(outcomes, expected, project):
    problems, indexed = [], {}
    if not isinstance(outcomes, list):
        return ['Manifest stage outcomes are not an array']
    for outcome in outcomes:
        if not isinstance(outcome, dict) or not isinstance(outcome.get('stageName'), str):
            problems.append('Malformed stage outcome')
            continue
        key = outcome['stageName'].casefold()
        if key in indexed:
            problems.append(f'Duplicate stage outcome: {key}')
        indexed[key] = outcome
    for stage, rule in expected.items():
        item = indexed.get(stage.casefold())
        if item is None:
            problems.append(f'Missing expected stage: {stage}')
            continue
        status = item.get('status')
        if status not in rule['statuses']:
            problems.append(f'{stage}: unexpected status {status!r}; reason={item.get("reasonCode")!r}')
        if str(status).casefold() == 'skipped' and item.get('reasonCode') not in rule.get('skip_reasons', []):
            problems.append(f'{stage}: skip reason not explicitly allowed')
        artifacts = item.get('artifactPaths', [])
        if not isinstance(artifacts, list):
            problems.append(f'{stage}: artifactPaths is not an array')
            continue
        if rule.get('require_artifacts', False) and not artifacts:
            problems.append(f'{stage}: no artifacts')
        for artifact in artifacts:
            try:
                path = confined(project, artifact)
                if not path.is_file() or path.stat().st_size == 0:
                    problems.append(f'{stage}: missing/empty artifact {artifact}')
            except (ValueError, TypeError, OSError) as exc:
                problems.append(f'{stage}: invalid artifact: {exc}')
    for key, item in indexed.items():
        if key not in {s.casefold() for s in expected} and str(item.get('status')).casefold() in {'failed', 'partiallysucceeded'}:
            problems.append(f'Unexpected unhealthy stage: {key}')
    return problems


def validate_probe(probe, expected_duration, tolerance):
    streams, errors = probe.get('streams', []), []
    for kind in ('video', 'audio'):
        if not any(s.get('codec_type') == kind for s in streams):
            errors.append(f'Export missing {kind} stream')
    try:
        duration = float(probe['format']['duration'])
        if not math.isfinite(duration) or duration <= 0 or abs(duration - expected_duration) > tolerance:
            errors.append('Export duration outside fixture tolerance')
    except (KeyError, ValueError, TypeError):
        errors.append('Export duration unavailable')
    return errors


def validate_config(config):
    for key in ('fixture_id', 'media', 'media_sha256', 'target_language', 'cli_command', 'expected_stages', 'expected_duration_seconds', 'duration_tolerance_seconds'):
        if key not in config:
            raise ValueError(f'Missing configuration field: {key}')
    if not isinstance(config.get('voice_clone', False), bool):
        raise ValueError('voice_clone must be an explicit JSON boolean')
    sha = config['media_sha256']
    if not isinstance(sha, str) or len(sha) != 64 or any(c not in '0123456789abcdef' for c in sha):
        raise ValueError('media_sha256 must be a lowercase SHA-256')
    stages = config['expected_stages']
    if not isinstance(stages, dict) or not stages or 'export' not in {s.casefold() for s in stages}:
        raise ValueError('Declare expected_stages including export')
    for rule in stages.values():
        if not isinstance(rule, dict) or not isinstance(rule.get('statuses'), list) or not rule['statuses'] or not all(isinstance(s, str) for s in rule['statuses']):
            raise ValueError('Each expected stage requires explicit status strings')
    for key, default in (('timeout_seconds', 1800), ('validation_timeout_seconds', 120), ('expected_duration_seconds', None)):
        value = float(config.get(key, default))
        if not math.isfinite(value) or value <= 0:
            raise ValueError(f'{key} must be finite and positive')
    tolerance = float(config['duration_tolerance_seconds'])
    if not math.isfinite(tolerance) or tolerance < 0:
        raise ValueError('duration_tolerance_seconds must be finite and nonnegative')


def git_identity(repo):
    def git(*args):
        result = subprocess.run(['git', '--no-optional-locks', '-C', str(repo), *args], capture_output=True, text=True, timeout=30, check=True)
        return result.stdout.strip()
    return {'head': git('rev-parse', 'HEAD'), 'working_tree': git('status', '--porcelain'), 'tracked_diff_sha256': hashlib.sha256(git('diff', 'HEAD').encode()).hexdigest()}


def run_worker(config_path, repo, evidence_root):
    config_path = Path(config_path).resolve()
    config = read_json(config_path)
    validate_config(config)
    repo = Path(repo).resolve()
    media = Path(config['media'])
    media = (config_path.parent / media).resolve() if not media.is_absolute() else media.resolve()
    if not media.is_file() or digest(media) != config['media_sha256']:
        raise ValueError('Input media missing or SHA-256 mismatch')
    evidence_root = Path(evidence_root).resolve()
    if evidence_root.is_relative_to(repo):
        raise ValueError('Evidence root must be outside repository')
    run = evidence_root / uuid.uuid4().hex
    run.mkdir(parents=True, exist_ok=False)
    project = run / 'project.trackdub'
    report = {'schema_version': 1, 'run_id': run.name, 'fixture': config, 'input_sha256': digest(media), 'python': sys.version, 'platform': sys.platform, 'problems': [], 'unverified': ['perceptual quality', 'actual provider/device attribution', 'resource budgets', 'stage semantic quality', 'resume behavior'], 'accepted': False, 'acceptance_scope': 'stage status/artifact presence and export structural decoding only'}
    try:
        report['code'] = git_identity(repo)
        report['execution'] = execute(build_command(config, media, project), repo, float(config.get('timeout_seconds', 1800)), run / 'pipeline')
        result = report['execution']
        if result['state'] != 'exited' or result['exit_code'] != 0:
            report['problems'].append(f'Pipeline {result["state"]}; exit={result["exit_code"]}')
        manifest_path = project / 'run-manifest.json'
        if not manifest_path.is_file():
            report['problems'].append('Run manifest missing')
        else:
            manifest = read_json(manifest_path)
            report['manifest'] = manifest
            report['problems'].extend(validate_stages(manifest.get('stageOutcomes'), config['expected_stages'], project))
        payload = None
        for line in (run / 'pipeline' / 'stdout.log').read_text(encoding='utf-8', errors='replace').splitlines():
            try:
                value = json.loads(line)
                if isinstance(value, dict) and 'manifestPath' in value:
                    payload = value
            except json.JSONDecodeError:
                continue
        if not payload or not payload.get('exportedFilePath'):
            report['problems'].append('CLI final export path missing')
        else:
            exported = confined(project, payload['exportedFilePath'])
            if not exported.is_file() or exported.stat().st_size == 0:
                report['problems'].append('Export missing or empty')
            else:
                budget = float(config.get('validation_timeout_seconds', 120))
                probe = execute([config.get('ffprobe', 'ffprobe'), '-v', 'error', '-show_streams', '-show_format', '-of', 'json', str(exported)], repo, budget, run / 'probe')
                report['probe_execution'] = probe
                if probe['state'] != 'exited' or probe['exit_code'] != 0:
                    report['problems'].append('Export probe failed')
                else:
                    report['problems'].extend(validate_probe(read_json(run / 'probe' / 'stdout.log'), float(config['expected_duration_seconds']), float(config['duration_tolerance_seconds'])))
                decode = execute([config.get('ffmpeg', 'ffmpeg'), '-v', 'error', '-xerror', '-nostdin', '-i', str(exported), '-map', '0:v:0', '-map', '0:a:0', '-f', 'null', '-'], repo, budget, run / 'decode')
                report['decode_execution'] = decode
                if decode['state'] != 'exited' or decode['exit_code'] != 0:
                    report['problems'].append('Export decode failed')
                report['export_sha256'] = digest(exported)
        report['accepted'] = not report['problems']
    except Exception as exc:
        report['problems'].append(f'{type(exc).__name__}: {exc}')
    finally:
        (run / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        summary = '# E2E technical validation\n\nAccepted: ' + str(report['accepted']) + '\n\n' + '\n'.join('- ' + x for x in report['problems']) + '\n\nUnverified:\n' + '\n'.join('- ' + x for x in report['unverified'])
        (run / 'summary.md').write_text(summary, encoding='utf-8')
    print(json.dumps({'accepted': report['accepted'], 'report': str(run / 'report.json')}))
    return 0 if report['accepted'] else 1


async def run_swerex(args):
    from swerex.deployment.local import LocalDeployment
    from swerex.runtime.abstract import Command
    config = read_json(args.fixture)
    validate_config(config)
    deployment = LocalDeployment()
    try:
        await deployment.start()
        timeout = float(config.get('timeout_seconds', 1800)) + 2 * float(config.get('validation_timeout_seconds', 120)) + 180
        argv = [sys.executable, str(Path(__file__).resolve()), 'worker', '--fixture', str(Path(args.fixture).resolve()), '--repo', str(Path(args.repo).resolve()), '--evidence-root', str(Path(args.evidence_root).resolve())]
        response = await deployment.runtime.execute(Command(command=argv, cwd=str(Path(args.repo).resolve()), timeout=timeout, check=False))
        print(response.stdout, end='')
        print(response.stderr, end='', file=sys.stderr)
        return response.exit_code if response.exit_code is not None else 1
    finally:
        await deployment.stop()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['worker', 'swerex'])
    parser.add_argument('--fixture', required=True)
    parser.add_argument('--repo', required=True)
    parser.add_argument('--evidence-root', required=True)
    args = parser.parse_args()
    return run_worker(args.fixture, args.repo, args.evidence_root) if args.mode == 'worker' else asyncio.run(run_swerex(args))


if __name__ == '__main__':
    raise SystemExit(main())
