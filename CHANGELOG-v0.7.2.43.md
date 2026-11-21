# Update Express 0.7.2.43

## Fix: multi-app Applications queue across reboot (#76)

Confirmed WinGet application installs now keep a durable remaining-package queue. If one package requires an OS restart, remaining upgrades stay queued with `ResumeAfterReboot` and continue automatically after boot when Application automatic is on.

Reminder auto-reboot is deferred while Applications automation is mid scan/prompt or a confirmed install queue is in flight, so a Påminnelse countdown cannot cut the queue short (same tray-poll race seen on REMOTESUPPORT1).

Also detects OS reboot-required WinGet exit codes / messages (ignoring “restart the application” only).

Codex P1 follow-up (`d669320`): `ResumeAfterReboot` waits for a real new OS boot (`ResumeBlockedAtBoot`); live-helper grace is based on boot stamp + mutex, not low uptime alone.

## Also in this dump tip

- WinGet / IM installs recorded in Historikk (#74 / 0.7.2.42)
- Prior 0.7.2.41 tip: Påminnelse footer realign, gear/proxy @18px, Fix IT header (#71/#70/#68)

Built from stacked tip `cursor/auto-app-install-queue-e625` (`d669320`) on #74 / 0.7.2.41.
