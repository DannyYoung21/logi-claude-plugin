# End User License Agreement (EULA)

**Claude Controller** (unofficial) – plugin for Logi Options+ / Logi Plugin Service

Effective date: October 17, 2026

This End User License Agreement ("Agreement") is between you ("User") and Daniele Mazzotta ("Developer")
and covers the unofficial plugin "Claude Controller" including updates ("Plugin").
By installing or using the Plugin, you agree to this Agreement. If you do not agree, do not install or use the Plugin.

## 1. License

The Developer grants you a free, non-exclusive, worldwide license to install and use the Plugin.

The Plugin's source code is published under the [MIT License](LICENSE). Nothing in this Agreement restricts the
rights the MIT License grants you. If this Agreement and the MIT License conflict, the MIT License prevails.

## 2. Third-party products

The Plugin is an **unofficial** community project. It is not affiliated with, endorsed by, or supported by
Anthropic, PBC or Logitech. "Claude" and "Claude Code" are trademarks of Anthropic, PBC; "Logitech", "Logi" and
"MX Creative Keypad" are trademarks of Logitech. Other names belong to their respective owners.

The Plugin requires the Claude desktop app and/or Claude Code, Logi Options+ and compatible Logitech hardware.
Your use of these products is governed by their own terms. The Plugin uses Claude Code hooks, local files of the
Claude desktop app and an interface for usage limits that is not publicly documented. Updates to Claude, Claude Code
or Logi Options+ may therefore change or break features of the Plugin at any time.

## 3. Changes to your Claude Code settings

To show live session status, the Plugin adds its own hooks and a statusline bridge to `~/.claude/settings.json`
when it starts. It changes only its own entries, keeps your original statusline command and keeps a backup of the
file. You can remove all of the Plugin's entries at any time with the "Claude Hooks On/Off" action or by hand as
described in the README.

## 4. Actions taken through the Plugin

The Plugin lets you control Claude Code sessions from your keypad, for example approving or denying a pending
action, approving it for the rest of a session or permanently (which adds a permission rule to your Claude Code
settings), stopping a session, or starting a new session or chat. These actions have the same effect as performing
them in Claude itself. **You are solely responsible for every action you approve, deny, stop or start through the
Plugin**, including any changes Claude makes to your files, code or system afterwards.

## 5. Privacy

The Plugin runs on your Mac. Its only network connection is a request to `api.anthropic.com` for the usage limits
of your own Claude account, authenticated with the Claude sign-in already stored on your Mac. The Plugin reads that
sign-in from the macOS Keychain for each request; it never stores, refreshes or shares it.

All other data stays on your computer: the Plugin receives Claude Code hook events on your own computer
(127.0.0.1), reads local files of the Claude desktop app and Claude Code, and keeps a small local cache.

The Developer does **not** collect, store, transmit, sell or share any personal data. The Plugin contains no
analytics, tracking or advertising.

The "Stop" action may ask macOS for the Accessibility permission for "Logi Plugin Service" so it can send Esc to the
Claude app. You can revoke this and the Keychain access at any time in macOS.

## 6. No warranty

The Plugin is provided **"as is" and "as available"**, without warranty of any kind, express or implied, including
warranties of merchantability, fitness for a particular purpose, accuracy and non-infringement, to the extent
permitted by applicable law. The Developer does not warrant that the Plugin will be error-free, uninterrupted, or
compatible with future versions of Claude, Claude Code, Logi Options+ or macOS. Values shown by the Plugin (such as
context usage, token counts and usage limits) are informational and may differ from the values in Claude.

## 7. Limitation of liability

To the maximum extent permitted by applicable law, the Developer is not liable for any indirect, incidental,
special or consequential damages, or for loss of data, profits, code or business, arising from the use of or
inability to use the Plugin, including damages resulting from actions approved or performed by AI agents.

This limitation does not apply to liability for intent or gross negligence, for injury to life, body or health,
or to any other liability that cannot be excluded or limited under mandatory law.

## 8. Support and updates

Support is provided voluntarily and without any guarantee of response time. The Developer has no obligation to
provide updates, fixes or new features, and may discontinue the Plugin at any time.

- Issues: https://github.com/DannyYoung21/logi-claude-plugin/issues
- Email: dannylabs.studio@gmail.com

## 9. Termination

You may end this Agreement at any time by switching the Plugin's hooks off and uninstalling the Plugin. The license
ends automatically if you breach this Agreement. Sections 4, 6, 7 and 10 survive termination.

## 10. General

The Developer may update this Agreement. The current version is published at
https://github.com/DannyYoung21/logi-claude-plugin/blob/main/EULA.md, with the effective date shown above.
Continuing to use the Plugin after an update means you accept the updated Agreement.

This Agreement is governed by the substantive laws of Switzerland, excluding the UN Convention on Contracts for
the International Sale of Goods (CISG) and the conflict-of-law rules. If you are a consumer, this choice of law does
not deprive you of the protection of mandatory provisions of the law of the country where you have your habitual
residence.

The place of jurisdiction for all disputes arising from this Agreement is the Developer's place of domicile in
Switzerland. If you are a consumer, any mandatory place of jurisdiction provided by law (in particular at your
domicile) remains unaffected.

If any provision of this Agreement is held invalid or unenforceable, the remaining provisions stay in full effect.

## 11. Contact

Daniele Mazzotta – dannylabs.studio@gmail.com
