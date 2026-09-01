# SDM browser extension

Right-click → **Download link with SDM**, or **Download image / video / audio with SDM**.
Whatever you picked goes to the running application, and SDM starts if it is not running.

## Why there is an entry per kind, not one entry

A right-click on an image inside a link reports both a link and a source. There is no rule
that resolves that correctly: on a thumbnail linking to the full picture the image is
wrong, and on an image linking to an article the link is wrong.

The first version preferred the link, which turned "download this image" into downloading
the product page wrapped around it. Chrome does not guess either — its own menu offers
"Save link as…" and "Save image as…" side by side — so neither does this. On a plain link
only one entry appears; on a linked image, both do, and you choose.

This is the whole extension for now. Taking over the browser's own downloads is
[Phase 4.3](../docs/roadmap.md), and forwarding cookies is Phase 4.4.

## Installing it

1. Build the solution, so `SDM.NativeHost.exe` exists beside `SDM.Desktop.exe`.
2. Register the host: `.\tools\install-native-host.ps1`
3. In Chrome, open `chrome://extensions`, turn on **Developer mode**, choose
   **Load unpacked**, and select this folder.

The id will be `efcijjodjgojhelobljfkbigkndfeobe`, which is what step 2 registered.

## Why the id is fixed

Chrome normally derives an unpacked extension's id from the folder it was loaded from, so
moving the repository would change the id and silently break the connection to the host —
the browser only ever says "host not found".

`manifest.json` therefore carries a `key`: the public half of a keypair. Chrome takes the
id from that key instead, so it is the same on every machine and after every reload, and
the registration in step 2 keeps working.

The private half is **not** in this repository. It was written to
`%LOCALAPPDATA%\SDM\keys\extension-signing-key.pem` and is needed only to pack a `.crx`
with this same id. Losing it costs nothing today: publishing through the Chrome Web Store
uses the store's own key, and generating a new keypair only means a new id and one more
run of step 2.

## Permissions, and why each one is here

| Permission | Why |
|---|---|
| `contextMenus` | The right-click entry. This is the feature. |
| `nativeMessaging` | The only way to reach a program outside the browser. |
| `notifications` | Failures are silent otherwise. Nothing is shown on success — SDM's own window is where a transfer belongs, and a notification per link would be noise. |

No `host_permissions`, no content scripts, no `tabs`: the extension never reads a page. It
sees the URL of the link that was right-clicked and the address of the tab it was in.

## When it does not work

The browser reports almost every failure as "host not found". In order of likelihood:

1. **The host is not registered.** Run `.\tools\install-native-host.ps1` again.
2. **The path is stale.** The host manifest records an absolute path to
   `SDM.NativeHost.exe`. Rebuilding elsewhere, or moving the repository, invalidates it —
   run the script again.
3. **The ids disagree.** Compare the id Chrome shows on `chrome://extensions` with
   `allowed_origins` in `com.sdm.host.json` beside the executable.

`chrome://extensions` → **service worker** opens the extension's console, where a failed
send is logged. `%LOCALAPPDATA%\SDM\logs` holds the application's side of the same
conversation.
