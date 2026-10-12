# Real-video E2E runner (development only)

Uses the production CLI and camelCase run manifest. Checks explicit expected
stage states, nonempty confined artifacts, export audio/video streams, duration,
and full FFmpeg decoding. No parallel pipeline implementation or automatic repair.

Use a disposable core checkout and its correctly built native CLI. Copy the
fixture example outside the repo and replace every placeholder: media/hash, CLI
DLL, models/provider options, stage expectations and duration. Stage status names
use manifest enum spelling. Skips need explicit skip_reasons. pipeline_args can
pin models/providers; it cannot override inputs/output, stage scope, preflight,
rerun policy, or voice-cloning consent through free-form arguments.

Voice cloning is disabled by default. Set the fixture field voice_clone to the
JSON boolean true for an explicitly requested cloning run; the runner then adds
--voice-clone exactly once. Strings and numbers are rejected. The report retains
this opt-in in its fixture configuration. This is a session setting, not a claim
about broader consent or licensing. The reusable example remains false; the
selected multi-speaker test fixture will use true as requested.

Commands from this directory:

    python -B -m unittest -v test_runner
    python runner.py worker --fixture <local.json> --repo <core-checkout> --evidence-root <outside-repo>
    python runner.py swerex --fixture <local.json> --repo <core-checkout> --evidence-root <outside-repo>

Install swe-rex only in a separate developer virtual environment for swerex mode.
This patch installs nothing and adds no product dependencies. SWE-ReX local mode
is NOT a sandbox. Its controller invokes the same native worker through the
one-off Command API. The native Windows smoke with SWE-ReX 1.4.0 failed at
import because pexpect.spawn is unavailable. Use worker mode until controller
compatibility is resolved; do not claim SWE-ReX execution is verified.

A unique run directory retains project artifacts, stdout/stderr, report.json and
summary.md. On worker timeout or KeyboardInterrupt, owned processes are killed
with taskkill /T on Windows or process-group kill on POSIX. Whole-controller
termination recovery and graceful cancellation/resume are follow-up work.

Acceptance is limited to declared stage state/artifact presence and export
structural validation. It does NOT establish transcript/translation quality,
naturalness, silence/clipping, actual provider attribution, resource budgets,
resume correctness, or full product readiness. Reports mark these unverified.
Input/config failures before execution fail immediately. Real-media inference
and SWE-ReX execution remain unverified. Rerun host tests after changes. No model
files, media uploads, commits, pushes, or threshold edits are performed.

Keep controlled BenchmarkEvidenceReport and BenchmarkDotNet artifacts separate.
Next slices: stronger stage semantic checks, diagnosis packets, bounded repairs,
corpus testing and stress/soak campaigns. Never edit gated/external/Trackdub.
