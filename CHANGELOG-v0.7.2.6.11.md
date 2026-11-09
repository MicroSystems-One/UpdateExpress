# Update Express v0.7.2.6.11

- Explicit, separate opt-in for interactive driver installation (one driver per operation).
- WUA uses ForceQuiet=false and AllowSourcePrompts=true only for drivers requiring interaction after user consent.
- Retains exact UpdateID + hardware ID recheck, firmware block, no automatic reboot, and existing logging.
- WUA may still reject or fail interactive drivers; an installer dialog is not guaranteed.
- Windows WPF build and runtime behavior must be validated on a Windows test machine.
