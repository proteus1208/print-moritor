# Production implementation roadmap

1. Validate PrintService event XML on supported Windows 10/11 builds. Event field names and ordering vary; do not rely on guessed Param indexes.
2. Add spooler queue notifications (`FindFirstPrinterChangeNotification`) and `GetJob` metadata.
3. Implement source-file correlation with per-application adapters (Word, Photoshop, Paint, PDF readers). Never infer an arbitrary path from a document title.
4. Implement explicit screenshot capture in the interactive Agent at job time, with user consent and a visible capture policy.
5. Add print-to-PDF/rendered-output capture through a supported print workflow or dedicated virtual printer. Do not assume raw SPL is a PDF.
6. Add authenticated named-pipe IPC between Agent/UI and service with ACLs restricted to the logged-in user and service SID.
7. Implement password enrollment, password verification, master-key wrapping, recovery-key flow, rate limiting, and key rotation.
8. Implement atomic archive job transactions: encrypt assets to staging, commit metadata, then finalize. Cleanup must reconcile orphan files and records.
9. Add temp extraction broker with per-session directories, restrictive ACLs, open-handle tracking, cleanup on close/crash/logoff/reboot.
10. Add robust quota accounting including index, journals, screenshots, assets, staging, and in-flight jobs. Protect active sessions and preserve database integrity.
11. Add WiX installer: elevated service installation, per-user Agent logon task, upgrade/uninstall, service recovery, ACLs, and event-log enablement.
12. Perform security review and test with real Epson R390 driver, Microsoft Print to PDF, Word, Photoshop, Paint, and common PDF viewers.
