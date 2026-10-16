# Local development and launch rules

- User preference: when asked to start Smoove, run scripts/Run-Settings.ps1 from this repository. The only current local application directory is out/app.
- Never launch the installed application, a desktop shortcut, or another output directory.
- A launch request does not authorize compilation, publishing, packaging, or installer creation. Use the existing verified local build.
- Before launch, verify local-build.json against source and application hashes. If source changed, refresh the required local binaries as part of authorized development and validate them before calling the result current. Never claim an older build contains newer edits.
- After authorized development, update out/app and its local-build.json together. Do not keep parallel dated or feature-named app builds.
- Preserve source, tests, assets, documentation, Git history, and user settings. Generated caches and test recordings are disposable.
