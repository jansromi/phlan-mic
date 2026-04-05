#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_icon="${repo_root}/assets/app-icons/ios/phlanmic-large.png"
output_dir="${repo_root}/apps/ios/PhlanMic.iOSClient/Assets.xcassets/AppIcon.appiconset"

if ! command -v sips >/dev/null 2>&1; then
  echo "error: sips is required to generate iOS app icons." >&2
  exit 1
fi

if [[ ! -f "${source_icon}" ]]; then
  echo "error: missing source icon at ${source_icon}" >&2
  exit 1
fi

generate_icon() {
  local pixels="$1"
  local filename="$2"
  sips -s format png -z "${pixels}" "${pixels}" "${source_icon}" --out "${output_dir}/${filename}" >/dev/null
}

generate_icon 40 iphone-notification-20@2x.png
generate_icon 60 iphone-notification-20@3x.png
generate_icon 58 iphone-settings-29@2x.png
generate_icon 87 iphone-settings-29@3x.png
generate_icon 80 iphone-spotlight-40@2x.png
generate_icon 120 iphone-spotlight-40@3x.png
generate_icon 120 iphone-app-60@2x.png
generate_icon 180 iphone-app-60@3x.png
cp "${source_icon}" "${output_dir}/ios-marketing-1024.png"

echo "Generated iOS app icons in ${output_dir}"
