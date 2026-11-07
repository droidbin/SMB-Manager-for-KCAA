# 2.0.5 patch review and correction

The original patch branch was reviewed against master ab39c0b and the original build37 source snapshot. The corrections keep marketing version 2.0.5, file version 2.0.145.0 and MSI version 2.0.145. Administrator/server deployment is excluded.

## Corrected behavior

- Install completion checks token elevation type rather than administrator group membership. A default token (including an administrator with UAC disabled) and a limited desktop token can complete; a full linked UAC token, session zero, service identities and token-query failures cannot certify the ordinary desktop session. Retry no longer forces the completion flag to true.
- Only an explicit `stable` key is labeled 정식. Current server endpoints can expose stable/beta. Legacy discovery falls back only on HTTP 404, retains the selected allowed legacy key and labels legacy catalogs beta. Authentication/approval errors do not fall back. This client change does not deploy the new server protocol or promote a legacy catalog to stable.
- Packaging keeps versions fixed, uses revision filenames, rejects existing candidate directories/distribution filenames, rejects reparse-point output paths, and never recursively deletes a caller-supplied directory. Final distribution creation uses CreateNew and verifies the signer.
- Same-version replacement uses AllowSameVersionUpgrades and REINSTALLMODE=emus with the existing transactional shutdown/late-removal order. It replaces equal or older files without downgrading newer runtime files. The MSI database verifier checks actual sequence numbers and replacement mode.
- Source metadata now describes the patch and hashes all 39 direct C# inputs. The original handover's .146 version increase, unconditional open-beta-to-stable display mapping and manual completion-flag workaround are superseded; do not use them as patch instructions or QA evidence.

## Validation and retained failures

Compilation succeeded without warnings/errors. Eighteen pure regression checks passed, covering token classifications, fail-closed behavior, explicit stable labeling, saved restricted selections and revoked permissions. These checks load no ClientEngine/ClientAgent runtime, do not install anything and do not perform SMB operations on the administrator PC.

r1 MSI SHA256: `0C7FF982A56EFE6F7751A451C3008E89737DFD2C281AEF060C62C0B5F6A2BE58`. Both dedicated guests returned MSI exit 0 but retained the build37 ClientDesktop.dll hash `7F8C79DE6F5F445DF03F6FD210580F9AFA39108055F911BA859CE75C9EFD2F20`. Rejected, not a passing update.

r2 MSI SHA256: `6F1FF40593D80BC4B48483973DD1F58FF21EEC354FA35164E6ABFD79A39CCBFE`. Both dedicated guests rejected the early removal sequence with MSI error 2613 / exit 1603; the original DLL remained. Rejected. Windows Installer requires early removal before actions generating the installation script; moving the deferred shutdown before removal violated that constraint. The early-removal approach is not the final implementation.

Encrypted pre-install recovery copies and the complete original logs remain under separate `C:\TEST\QA38-Patch-*` and `C:\TEST\QA38-Patch-r2-*` guest folders. No historical failure is relabeled Pass.

## Final r3 candidate measurements

Signed MSI: `SMB-Manager-Client-2.0.5-Patch-r3-x64.msi`; SHA256 `32F583803D0A6A063A6DE00764ED014A6B9BA322DA84FF0349E49144F80CBB88`.
Installed signed ClientDesktop.dll SHA256: `BF0C3F660D9C585E287D123C51DE6A2E256C5E9BB2C0DDBFE66E351545F86DE2`.

The MSI signature was Valid for KCAA(LEE HYEONGBIN). Database inspection verified version 2.0.145, REINSTALLMODE=emus and sequence 1500 InstallInitialize / 1501 CloseInstalledAppsElevated / 4000 InstallFiles / 6501 RemoveExistingProducts.

| Changed check | Windows 10 KCAA-W10-01/KCAALabs | Windows 11 KCAA-W11-01/KCAALabs |
| --- | --- | --- |
| Actual same-version MSI replacement; exact new DLL installed | Pass, exit 0 | Pass, exit 0 |
| Device ID/key, public key, server URL, name, department, computer name retained | Pass, all seven fields equal | Pass, all seven fields equal |
| Ordinary desktop completion and one actual client instance | Pass | Pass |
| Existing assigned drive mapping and designated real read/write | Pass | Pass |
| Separate Samba server account and held probe files observed | Pass, qaops at 10.77.40.10 | Pass, qaops at 10.77.40.10 |
| Own SMB probe files cleaned | Pass | Pass |
| App relaunch retains completion without rerunning cleanup | Pass | Pass |
| Actual reboot / actual UAC-disabled desktop | NotRun | NotRun |

The encrypted identity file hash changed after launch because the protected file was rewritten. That byte comparison is retained as false in the raw install report, not called data loss: the independent field comparison against the encrypted recovery ZIP verified every listed registration field unchanged without exporting their values.

The first r3 Windows 10 connection observation found no mappings while the existing dedicated management-server task was stopped. Its failure is retained. The authorized dedicated task KCAA-QA29-R2-HttpsServer was started, health build29 was checked, and only the outstanding connection observation resumed. Windows 10 attempt2 and Windows 11 attempt1 passed. The operational management server was untouched.

Local raw evidence remains in the primary workspace `.local/qa38-patch-r3`; encrypted recovery copies and guest observations remain in `C:\TEST\QA38-Patch-r3-Windows10` and `C:\TEST\QA38-Patch-r3-Windows11`. No administrator desktop was installed/run on the test host or guests. The administrator PC performed source builds, pure-rule checks and MSI database inspection only.

## Pending release checks

This is a source correction and candidate verification record, not a new 90% release assessment. Actual UAC-disabled desktop behavior and a fresh reboot are not claimed from pure-rule or application-relaunch checks. D01 GitHub candidate publication/download and D02 exact authenticated catalog delivery/revision-aware in-app update are not marked Pass. CB04 recovery approval and the final source-bound check ledger still require candidate review; the old verified stable recovery MSI is retained.

The installed build37 updater compares file versions and cannot automatically discover another 2.0.145.0 package. The unique MSI permits an explicit same-version installation; revision-aware automatic delivery requires a separate compatible server/client change. The existing operational catalogs, permissions, release assets and tags have not been changed by this correction. Do not call the new patch publicly delivered merely because source was merged or a signed MSI exists.

References: [Microsoft token elevation types](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_elevation_type), [Microsoft REINSTALLMODE](https://learn.microsoft.com/en-us/windows/win32/msi/reinstallmode), [WiX MajorUpgrade](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/).
