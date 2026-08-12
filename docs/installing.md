# Installing ERP Doctor

ERP Doctor ships self-contained Windows x64 and Linux x64 archives. These archives do not require the .NET SDK or a preinstalled .NET runtime.

The v0.15 installer scripts are designed around one rule: **verify the release archive SHA256 before extracting or installing it**.

> The repository does not publish a real release until a maintainer intentionally creates a version tag. The commands below that use `latest` become usable after the first GitHub Release is published.

## Windows x64

The release asset is:

```text
erp-doctor-win-x64.zip
```

The recommended installer is `install.ps1`. Download the installer script from the GitHub Release, inspect it, then run it with Windows PowerShell or PowerShell 7:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Install a specific release:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Version v0.15.0
```

Custom destination:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -InstallDir C:\Tools\erp-doctor
```

Default destination:

```text
%LOCALAPPDATA%\Programs\erp-doctor
```

By default the installer adds the destination to the current user's PATH and to the current process PATH. A new terminal may be required before other shells see the user PATH change.

To avoid any PATH mutation:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoPathUpdate
```

The installer does not clear a custom destination directory. It only copies/overwrites files contained in the verified ERP Doctor archive.

## Linux x64

The release asset is:

```text
erp-doctor-linux-x64.tar.gz
```

Download `install.sh` from the GitHub Release, inspect it, then run:

```bash
bash install.sh
```

Install a specific release:

```bash
bash install.sh --version v0.15.0
```

Custom destination:

```bash
bash install.sh --install-dir /opt/erp-doctor/bin
```

Default destination:

```text
~/.local/bin
```

The Linux installer deliberately does not edit `.bashrc`, `.zshrc`, or other shell startup files. If the destination is not already on PATH, it prints the directory that should be added.

## What is verified

Both installers obtain `checksums.txt` from the same GitHub Release as the platform archive and require a matching SHA256 entry before extraction.

Windows uses:

```powershell
Get-FileHash -Algorithm SHA256
```

Linux uses:

```bash
sha256sum
```

A checksum mismatch stops installation before archive extraction.

The release workflow also publishes `install.ps1` and `install.sh` as release assets and includes both installer files in `checksums.txt`. Review/download the installer before executing it; the archive checksum protects the archive that the installer consumes.

## Offline or CI installation

Both scripts support local release artifacts. This is also how ERP Doctor validates installer behavior without publishing a real release.

Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 `
  -ArchivePath .\erp-doctor-win-x64.zip `
  -ChecksumsPath .\checksums.txt `
  -InstallDir .\erp-doctor-local `
  -NoPathUpdate
```

Linux:

```bash
bash scripts/install.sh \
  --archive ./erp-doctor-linux-x64.tar.gz \
  --checksums ./checksums.txt \
  --install-dir ./erp-doctor-local
```

## Updating

Re-run the installer with `latest` or a newer explicit tag. Verified files from the newer release overwrite files with the same names in the destination.

## Uninstalling

There is intentionally no privileged uninstall workflow in v0.15.

- Linux: remove the installed `erp-doctor` file/directory from the chosen destination.
- Windows: remove the ERP Doctor installation directory and, if the installer added it, remove that directory from the user PATH.

## Current platform scope

Self-contained installer UX currently targets:

```text
Windows x64
Linux x64
```

Other architectures/platforms should use source/global-tool workflows until dedicated release artifacts are added and validated.
