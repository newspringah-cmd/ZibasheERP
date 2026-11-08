The production systemd units run `/usr/local/sbin/zibashe-b2-backup.py full|log`.
`/etc/zibashe-backup/b2.json` remains on the server and is never copied into the
repository. The existing hourly log and daily full schedules remain unchanged.

SQL Server creates each `.bak` or `.trn` with CHECKSUM and runs RESTORE VERIFYONLY.
The uploader then creates a deterministic gzip file, verifies that decompressing
it exactly reproduces the original byte count and SHA-256, and uploads `.bak.gz`
or `.trn.gz`. S3 metadata records both compressed and original checksums and the
original byte count. A failed compression or upload retains the original file.

The existing remote lifecycle keeps backups for 30 days. Local originals and
compressed copies remain for 28 days and are removed only when remote metadata
matches the original. Existing uncompressed remote backups remain supported.
No cloud object is deleted by this uploader.

To restore a compressed backup, download it to a protected server directory,
verify its compressed SHA-256 against S3 metadata, decompress with `gzip -dc`
into a mode-600 `.bak` or `.trn`, then verify its original SHA-256 and size against
metadata. Run RESTORE VERIFYONLY WITH CHECKSUM and follow the normal isolated
restore procedure, preserving the complete full/log chain. A gzip archive is
not directly usable as a SQL Server backup device.

Run isolated synthetic tests with `python3 deploy/tests/backup-b2.test.py`.
