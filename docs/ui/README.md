# Workspace UI review

This change applies a shared visual treatment to Mail, Calendar, Files, Notes, People, and To Do. It is based on the reliability/onboarding branch in PR #2.

## Design references

These are observed patterns in established products, not a formal UI standard:

- [Notion Mail](https://mobbin.com/screens/6cbf9d6c-b0d2-4d89-9ee5-57bbd6fec0cf): quiet navigation, clear unread hierarchy, and a separate reading area.
- [Cloaked inbox](https://mobbin.com/screens/be1d5c4b-2c3a-4173-b04d-5aa37f46609c): distinct navigation, message list, and reading columns.
- [Google Drive](https://mobbin.com/screens/4e58234e-a5d7-4119-8ab6-f4d7dbc4f20d): consistent navigation and search, recognizable file icons, and aligned file metadata.
- [Skiff Calendar](https://mobbin.com/screens/14bee891-46ef-4335-b911-15b68ada21f7): compact controls around a spacious weekly grid.

BetterMail keeps its existing workflows and commands. The implementation introduces labeled vector navigation, light/dark surface colors, consistent search and action styling, keyboard-visible mail quick actions, portable file icons, readable Google Drive labels, and a wider calendar sidebar. The empty mail reader's visibility now binds to the main window rather than the nested conversation model, avoiding overlapping placeholders.

## Actual app screenshots

Captured from the real Avalonia views on Linux at 1440 × 960 using fictional offline data. Mail and Notes show overview/navigation states; the capture environment does not reliably render the embedded native web content. These are screenshots, not mockups. Disabled controls reflect the read-only fixture. No connected account data or credentials are used.

| Workspace | Light | Dark |
| --- | --- | --- |
| Mail | [Screenshot](screenshots/mail-light.png) | [Screenshot](screenshots/mail-dark.png) |
| Calendar | [Screenshot](screenshots/calendar-light.png) | [Screenshot](screenshots/calendar-dark.png) |
| Files | [Screenshot](screenshots/files-light.png) | [Screenshot](screenshots/files-dark.png) |
| Notes | [Screenshot](screenshots/notes-light.png) | [Screenshot](screenshots/notes-dark.png) |
| People | [Screenshot](screenshots/people-light.png) | [Screenshot](screenshots/people-dark.png) |
| To Do | [Screenshot](screenshots/todos-light.png) | [Screenshot](screenshots/todos-dark.png) |

[390-pixel mail layout](screenshots/mail-phone.png)

## Reproduce

From the repository root on Linux with the normal Avalonia dependencies, Python 3, and Xvfb installed:

```sh
dotnet build tools/BetterMail.UiPreview -c Release -m:1
xvfb-run -a -s '-screen 0 1440x960x24' dotnet run --project tools/BetterMail.UiPreview -c Release --no-build -- docs/ui/screenshots
```

The preview is a separate development tool, not a production app mode. It loads the real application resources and view models with an offline provider that rejects writes, uses a temporary profile, and captures X11 pixels (no additional Python packages). Calendar samples use the current day so they remain visible when regenerated. The preview project is Linux-only and is built by Linux CI; it is not shipped in application packages.
