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

## Taking over the browser's downloads

On by default: someone installing a download manager's extension is asking for its
download manager. The options page turns it off, and the change applies to the next click
rather than the next restart.

**A download is paused, not cancelled, until SDM has accepted it.** If the host is
unregistered, the path is stale, or SDM refuses, the browser's own download is resumed and
Chrome finishes it, with a notification saying why. Cancelling first would mean one broken
component silently breaks every download in the browser.

Only `http` and `https` are taken over. A `blob:` or `data:` URL exists nowhere but inside
the page that created it, and handing one to SDM would cancel a download that nothing else
can then perform.

A download that finishes before the handover completes is also left alone. Reaching SDM
means launching a process and waiting on a pipe, and a small file can be finished by then —
at which point taking it over would fetch a second copy of a file the browser already has.

## The real request is copied, not guessed

Sending a cookie, a referer and a user-agent is a guess about which three headers matter.
It is often right and sometimes badly wrong: an application API answers the request its own
page made, and what makes that request acceptable may be a header nobody outside the site
could name — a client build id, a workspace id, a token that was never a cookie.

So the extension watches the request go out and copies all of it. SDM applies the whole set,
except the handful the transfer itself owns: `Range` and `If-Range` are how a download is
split and resumed, and `Accept-Encoding` is deliberately dropped because SDM turns automatic
decompression off — honouring the browser's gzip would write compressed bytes into the file
and call it finished.

If a site still refuses, the options page takes a list of hostnames whose downloads stay
with the browser. Subdomains are included: `claude.ai` also covers `api.claude.ai`.

## The session travels with the download

Chrome sends your cookies with every download it makes. SDM fetching the same URL from
another process is a different visitor, so the extension sends the cookie header, the
referring page and the browser's user-agent along with the URL. Without them a file behind
a login comes back as the sign-in page, saved under the name of the file you wanted.

SDM holds that context in memory for the transfer and **never writes it to disk**. A
cookie is a credential; a list of downloads is not the place to keep live sessions. The
cost is that a transfer resumed after restarting SDM goes without it and may fail — which
is the better of the two failures.

## Permissions, and why each one is here

| Permission | Why |
|---|---|
| `contextMenus` | The right-click entries. |
| `nativeMessaging` | The only way to reach a program outside the browser. |
| `downloads` | Pausing, cancelling and resuming the browser's own downloads. |
| `cookies` | The session that makes a protected download work at all. |
| `storage` | Remembering the one setting on the options page. |
| `webRequest` | Watching the real request go out, so its headers can be copied rather than guessed. Observation only — MV3 forbids blocking listeners, and nothing here needs one. |
| `notifications` | Failures are silent otherwise. Nothing is shown on success — SDM's own window is where a transfer belongs, and a notification per download would be noise. |
| `<all_urls>` | `chrome.cookies` is per-site: reading the cookies for a download means host access to the site it came from, and a download can come from anywhere. |

No content scripts and no `tabs`: the extension never reads the contents of a page. It
sees the URL being downloaded, the address of the tab it came from, and that site's
cookies.

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
