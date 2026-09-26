# Modal system

_Category: [Frontend UI shell](../../DOCUMENTATION_CHECKLIST.md) · Last verified against code: 2026-09-25_

## What it does

A single shared `<app-modal>` host, driven by `ModalService`, lets any component project its own content into
one dialog shell with dynamic footer buttons — used for everything from the equalizer editor to delete
confirmations — plus a custom `confirm()` helper that replaces `window.confirm()` with an app-styled dialog.

## Two-part architecture: content projection + config-driven footer

```mermaid
flowchart TD
    subgraph Caller["Any component"]
        project["modalService.projectComponent(SomeComponent)<br/>— creates SomeComponent inside <app-modal>'s ViewContainerRef host"]
        configure["modalService.openDialog(new ModalConfig(title, buttons))<br/>— sets the shell's title + footer buttons"]
    end

    subgraph Shell["ModalComponent (<app-modal>, one shared instance)"]
        host["ViewContainerRef host — whatever was projected renders here"]
        footer["dynamic footer — one button per ModalButtonConfig, plus always-present Cancel/X"]
    end

    subgraph Service["ModalService (shared, providedIn: root)"]
        openDialog$["openDialog$ — ModalComponent subscribes, updates title/buttons"]
        requestClose$["requestClose$ — lets a caller force-close programmatically"]
        dialogClosed$["dialogClosed$ — ModalComponent fires on any close, however it happened"]
    end

    project --> host
    configure --> openDialog$ --> footer
    Shell -.->|Cancel/X clicked, or requestClose() received| dialogClosed$
```

The content a dialog shows and the buttons/title around it are configured independently: `projectComponent<T>(componentType)`
clears the shared host (`ViewContainerRef`) and dynamically creates the given component inside it — this is how
`EqualizerComponent`, the delete-confirmation body, and any other dialog body gets rendered without `ModalComponent`
needing a case for every possible dialog type. `openDialog(modalConfig)` separately pushes a `ModalConfig`
(title + `ModalButtonConfig[]`) through `openDialog$`, which `ModalComponent` subscribes to and uses to render
its footer — Cancel/X is always present regardless of what's passed in; every entry in `buttons` is an
*additional* action (typically one primary action, e.g. "Save" or "Delete").

`ModalButtonConfig` bundles everything a footer button needs: `text`, `icon`, `color`, a `ModalButtonType`
(`primary`/`secondary`, driving styling), and a `callback: () => any` invoked on click — the calling component
supplies its own button-click logic directly as a closure, so `ModalComponent` never needs to know what any
given button actually does.

## Re-calling `openDialog()` for dynamic footer buttons

A dialog whose available actions change based on its own internal state (documented precedent: a dialog that
only gains a "Save" button once it determines nothing else was found — see [Synced lyrics
display](../../features/04-lyrics/synced-lyrics-display.md) for the underlying use case) re-invokes `openDialog()`
with a new `ModalConfig` carrying an updated button list, without re-projecting the body component or otherwise
disturbing what's already rendered inside the host — `openDialog$`/`ModalComponent`'s footer subscription and
`projectComponent`/the host content are two independent channels, so updating one doesn't touch the other.

## `confirm()`: a promise-based replacement for `window.confirm`

`ModalService.confirm(title, message, options?)` returns a `Promise<boolean>`, resolving `true` only if the
dialog's single confirm button is clicked, and `false` for every other way the dialog closes (Cancel, the X
button, or any other path that ends up calling `notifyClosed()`). Internally: it projects a small
message-only `ConfirmDialogComponent` into the shared host (the same projection mechanism any other dialog
uses), subscribes once to `dialogClosed$` to catch the "user backed out" case (resolving `false`, since a
`ModalButtonConfig`'s callback has no return value to key a resolution off of on its own), and opens a
single-button `ModalConfig` whose callback resolves `true` and then calls `requestClose()` to close the dialog
programmatically — `ModalComponent` otherwise only closes itself in response to its own Cancel/X, so a
successful confirm needs this explicit signal to close the dialog from outside.

This replaced browser-native `window.confirm()` specifically so delete confirmations could match the app's own
visual style (custom icon/color per call via `ConfirmOptions`, defaulting to a red bin icon reading "Delete")
rather than an unstyleable native browser dialog.

## Known constraints

- Only one dialog can be open at a time — `projectComponent` clears the host before creating the new component,
  so opening a second dialog while one is already open replaces it rather than stacking.
- `confirm()`'s `settled` flag guards against a double-resolve if both the confirm callback and a subsequent
  `dialogClosed$` emission fire in quick succession (the confirm path explicitly calls `requestClose()`, which
  itself triggers a close and therefore a `dialogClosed$` emission) — without it, the promise could theoretically
  settle twice, though only the first resolution would ever have an effect either way.
