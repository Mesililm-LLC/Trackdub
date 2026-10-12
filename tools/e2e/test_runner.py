from pathlib import Path
import sys
import tempfile
import unittest
import runner


class ValidatorTests(unittest.TestCase):
    def test_success_requires_artifact(self):
        with tempfile.TemporaryDirectory() as root:
            item = {'stageName': 'ASR', 'status': 'succeeded', 'artifactPaths': []}
            self.assertTrue(runner.validate_stages([item], {'asr': {'statuses': ['succeeded'], 'require_artifacts': True}}, root))

    def test_valid_stage(self):
        with tempfile.TemporaryDirectory() as root:
            Path(root, 'transcript.json').write_text('{}')
            item = {'stageName': 'ASR', 'status': 'succeeded', 'artifactPaths': ['transcript.json']}
            self.assertEqual([], runner.validate_stages([item], {'asr': {'statuses': ['succeeded'], 'require_artifacts': True}}, root))

    def test_missing_stage(self):
        self.assertTrue(runner.validate_stages([], {'tts': {'statuses': ['succeeded']}}, '.'))

    def test_partial_not_success(self):
        self.assertTrue(runner.validate_stages([{'stageName': 'TTS', 'status': 'partiallySucceeded'}], {'tts': {'statuses': ['succeeded']}}, '.'))

    def test_skip_requires_explicit_reason(self):
        item = {'stageName': 'VAD', 'status': 'skipped', 'reasonCode': 'no_speech'}
        self.assertTrue(runner.validate_stages([item], {'vad': {'statuses': ['skipped']}}, '.'))
        self.assertEqual([], runner.validate_stages([item], {'vad': {'statuses': ['skipped'], 'skip_reasons': ['no_speech']}}, '.'))

    def test_duplicate_stage(self):
        item = {'stageName': 'ASR', 'status': 'succeeded'}
        self.assertTrue(runner.validate_stages([item, item], {'asr': {'statuses': ['succeeded']}}, '.'))

    def test_unexpected_failed_stage(self):
        self.assertTrue(runner.validate_stages([{'stageName': 'Other', 'status': 'failed'}], {}, '.'))

    def test_artifact_escape(self):
        with tempfile.TemporaryDirectory() as root:
            with self.assertRaises(ValueError):
                runner.confined(root, '../outside.wav')

    def test_empty_artifact(self):
        with tempfile.TemporaryDirectory() as root:
            Path(root, 'empty.wav').touch()
            self.assertTrue(runner.validate_stages([{'stageName': 'TTS', 'status': 'succeeded', 'artifactPaths': ['empty.wav']}], {'tts': {'statuses': ['succeeded'], 'require_artifacts': True}}, root))

    def test_probe_duration_and_streams(self):
        good = {'streams': [{'codec_type': 'video'}, {'codec_type': 'audio'}], 'format': {'duration': '10.0'}}
        self.assertEqual([], runner.validate_probe(good, 10, 0.5))
        self.assertTrue(runner.validate_probe(good, 15, 0.5))
        good['format']['duration'] = 'nan'
        self.assertTrue(runner.validate_probe(good, 10, 0.5))
        self.assertTrue(runner.validate_probe({}, 10, 0.5))

    def test_command_is_bounded(self):
        config = {'cli_command': ['dotnet', 'app.dll'], 'target_language': 'es'}
        argv = runner.build_command(config, 'path with spaces/video.mp4', 'new project')
        self.assertIn('path with spaces/video.mp4', argv)
        self.assertIn('--force-rerun', argv)
        for flag in ['--output', '--skip-preflight', '--voice-clone', '--only']:
            config['pipeline_args'] = [flag + '=anything']
            with self.assertRaises(ValueError):
                runner.build_command(config, 'v', 'p')

    def test_missing_config(self):
        with self.assertRaises(ValueError):
            runner.validate_config({})

    def test_process_exit_and_logs(self):
        with tempfile.TemporaryDirectory() as root:
            result = runner.execute([sys.executable, '-c', "import sys; print('evidence'); sys.exit(7)"], root, 5, Path(root, 'logs'))
            self.assertEqual(7, result['exit_code'])
            self.assertEqual('exited', result['state'])
            self.assertIn('evidence', Path(root, 'logs', 'stdout.log').read_text())

    def test_timeout_is_not_success(self):
        with tempfile.TemporaryDirectory() as root:
            result = runner.execute([sys.executable, '-c', 'import time; time.sleep(30)'], root, 0.15, Path(root, 'logs'))
            self.assertEqual('timed_out', result['state'])
            self.assertNotEqual(0, result['exit_code'])

    def test_bad_manifest_shape(self):
        self.assertTrue(runner.validate_stages(None, {}, '.'))
        self.assertTrue(runner.validate_stages([None], {}, '.'))

    def test_voice_clone_default_and_opt_in(self):
        config = {'cli_command': ['dotnet', 'app.dll'], 'target_language': 'es'}
        self.assertNotIn('--voice-clone', runner.build_command(config, 'v', 'p'))
        config['voice_clone'] = False
        self.assertNotIn('--voice-clone', runner.build_command(config, 'v', 'p'))
        config['voice_clone'] = True
        self.assertEqual(1, runner.build_command(config, 'v', 'p').count('--voice-clone'))

    def test_voice_clone_rejects_non_boolean(self):
        config = {'cli_command': ['dotnet', 'app.dll'], 'target_language': 'es'}
        for value in ['false', 'true', 1, None]:
            config['voice_clone'] = value
            with self.assertRaises(ValueError):
                runner.build_command(config, 'v', 'p')


if __name__ == '__main__':
    unittest.main()
