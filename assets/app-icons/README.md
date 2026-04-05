# App Icon Sources

Drop your source images here, then regenerate the platform-specific app icons:

- iOS large source: `assets/app-icons/ios/phlanmic-large.png`
- iOS small source: `assets/app-icons/ios/phlanmic-small.png`
- Windows large source: `assets/app-icons/windows/phlanmic-large.png`
- Windows small source: `assets/app-icons/windows/phlanmic-small.png`

Generation commands:

```sh
./scripts/generate-ios-app-icons.sh
python3 ./scripts/generate-windows-app-icon.py
```

Generated outputs:

- iOS asset catalog icons: `apps/ios/PhlanMic.iOSClient/Assets.xcassets/AppIcon.appiconset/`
- Windows executable icon: `apps/windows-host/src/PhlanMic.WindowsHost.Ui/Assets/AppIcon/app.ico`

Notes:

- The `phlanmic-large.png` files should be square `1024x1024` PNGs.
- The `phlanmic-small.png` files should be square `64x64` PNGs.
- The iOS generator uses the large image.
- The Windows generator uses the large image for the high-resolution `.ico` slots and the small image for the compact slots.
