# Security

Dashio changes how a PC starts, ends programs, starts uninstallers and can delete files, and
part of it runs with administrator rights. A flaw here matters, so reports are welcome.

## Reporting a problem

Please do not open a public issue for something that could be misused. Use GitHub's
**Report a vulnerability** button on the repository's Security tab, which reaches the
maintainer privately. Say what you did, what happened and which version you used.

You can expect a first answer within a week. A fix is released before the details are made
public, and you are credited unless you ask not to be.

## What is in scope

- Getting `Dashio.Helper` to do anything beyond what it is meant to: it runs elevated, and it
  accepts only item ids with target states, process ids with start times, and a drive letter.
  Making it run a command, open a path it was given, or act on a Windows component is a
  vulnerability.
- Getting Dashio to end, switch off, uninstall or delete something the user did not choose.
- Reading or changing another user's data through Dashio.
- Dashio connecting to the network at all. It is meant never to.

## What is not

- Anything that needs an attacker who is already an administrator on the PC.
- The installer and the builds not being signed yet. That is known and being worked on.

## Supported versions

Only the latest release gets fixes.
