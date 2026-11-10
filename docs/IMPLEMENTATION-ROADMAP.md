# Implementation status

## Done in this tree

1. SQLite job database with required fields + duplicate protection
2. History UI wired to live archive (search / date / printer / open / export / delete)
3. Password enrollment, unlock, change (wraps master key)
4. AES-256-GCM document encryption + DPAPI service key
5. Best-effort original-file capture
6. 5 GB default quota + cleanup
7. Temp decrypted-file lifecycle + orphan cleanup
8. Self-contained publish + Setup.ps1 installer

## Remaining / known gaps

1. Per-application deep source correlation (Word COM, Photoshop, etc.)
2. Rendered-output / virtual-printer PDF capture
3. Interactive screenshot capture with user consent
4. Authenticated named-pipe IPC between Agent/UI and service
5. Recovery-key flow, rate limiting, key rotation
6. Full WiX MSI packaging beyond Setup.wxs skeleton
7. Security review against real printers/drivers
