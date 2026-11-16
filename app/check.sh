#!/bin/bash
# Runs the standalone checks: app/check.sh [name ...]; no names runs all, `--list` prints the names.
# Each line below is a check's whole dependency set, so a Foundation-only module stays provably
# Foundation-only (those checks also build on Linux). "app" means every app source (macOS only).
set -u
. "$(dirname "$0")/sources.sh"
text="CleanupTypes CleanupText CleanupGuard RuleCleaner PrivateFiles Dictionary Learning Retention"
providers="CleanupTypes CleanupText CleanupGuard CleanupPrompt RuleCleaner ProviderTypes ProviderHTTP OpenAICompatible Gemini Credentials"
cloud="$text Cleanup CleanupPrompt ProviderTypes ProviderHTTP OpenAICompatible Gemini Credentials Processing WritingModes"
meetings="Retention CleanupText CallApps Meetings MeetingStore MeetingExport MeetingNotes MeetingSpeakers"
models="Digests ModelManifest ModelDownload"
deps() {
  case "$1" in
    launch) echo "LaunchOptions" ;;
    history) echo "Capture CleanupTypes CleanupText Retention History ModelFiles" ;;
    controller) echo "app ../spike/InsertionChecks" ;;
    meeting_start) echo "app" ;;
    recording) echo "Shortcut Capture Session" ;;
    speaker_flow) echo "Capture CleanupTypes History ModelFiles ModelText $models $meetings MeetingCapture MeetingRecorder SpeakerModelSettings" ;;
    delivery) echo "Delivery" ;;
    dictionary) echo "$text" ;;
    processing) echo "$cloud Observation" ;;
    modes) echo "$cloud" ;;
    apple_cleanup) echo "$cloud Routes CloudAudio AppleCleanup" ;;
    routes) echo "$cloud Routes CloudAudio KeyCheck AppleCleanup" ;;
    providers) echo "$providers" ;;
    insights) echo "Insights" ;;
    meetings|speakers) echo "$meetings" ;;
    call_detection) echo "CallApps CallDetection" ;;
    model_download) echo "$models" ;;
    app_services) echo "Retention $models ModelText HistoryRouter WelcomeGuide SettingsPanes AppUpdater" ;;
    boost_filter) echo "../module-one/models/Runner/Sources/ModelBench/BoostFilter" ;;
    cleanup) echo "Cleanup CleanupTypes CleanupText CleanupGuard CleanupPrompt RuleCleaner" ;;
    *) return 1 ;;
  esac
}
swift_checks="launch history controller meeting_start recording speaker_flow delivery dictionary processing modes apple_cleanup routes providers insights meetings speakers call_detection model_download app_services boost_filter cleanup"
script_checks="http update_config"
if [ "${1:-}" = "--list" ]; then echo $swift_checks $script_checks; exit 0; fi
out="${TMPDIR:-/tmp}/murmur-checks"; mkdir -p "$out"; cd "$project_root" || exit 1
failed=""
run() {
  name="$1"
  case "$name" in
    http) python3 app/check_http.py; return ;;
    update_config) python3 app/tools/check_update_config.py; return ;;
  esac
  list="$(deps "$name")" || { echo "unknown check: $name (try --list)" >&2; return 1; }
  files=()
  for d in $list; do
    if [ "$d" = app ]; then files+=("${app_sources[@]}"); else files+=("app/$d.swift"); fi
  done
  swiftc -swift-version 5 "${files[@]}" "app/check_$name.swift" -o "$out/$name" || return 1
  if [ "$name" = cleanup ]; then python3 app/tools/build_cleanup_corpus.py && "$out/$name" app/cleanup-corpus.json; else "$out/$name"; fi
}
for name in ${@:-$swift_checks $script_checks}; do
  if run "$name" > "$out/$name.log" 2>&1; then echo "PASS $name"; else echo "FAIL $name (see $out/$name.log)"; failed="$failed $name"; fi
done
[ -z "$failed" ]
