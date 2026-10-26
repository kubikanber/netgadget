# Contributing

**English** · [Türkçe](CONTRIBUTING.tr.md)

Thanks for wanting to contribute to NetGadget! Bug reports, ideas, translations, documentation fixes or code —
all of it is valuable.

## Where to start

- **Found a bug?** Open a [bug report](https://github.com/kubikanber/netgadget/issues/new?template=hata-bildirimi.yml).
- **Have an idea?** Check the [Roadmap](README.md#roadmap) in the README first, then open a
  [feature request](https://github.com/kubikanber/netgadget/issues/new?template=ozellik-onerisi.yml).
- **Want to add your language?** See [Adding a language](#adding-a-language).
- **Found a security vulnerability?** Don't open an issue; use the private reporting described in [SECURITY.md](SECURITY.md).
- Planning a bigger change? Let's talk in an issue first so your effort isn't wasted.

Issues and pull requests can be written in **English or Turkish**.

## Project structure

| File | Purpose |
|---|---|
| `Baslat.cmd` | Launcher; starts PowerShell with the right parameters |
| `NetGadget.ps1` | WPF interface, menu, settings, language loading, administrator prompt |
| `NetSampler.cs` | ETW session (Kernel-Network, DNS-Client), TCP tables, EStats, name resolution, app grouping; the `Loc` translation class |
| `Inspector.cs` | Digital signature, process details (WMI), suspicion score rules, "first seen" and the trusted list |
| `lang/*.json` | Interface texts (key → translation) |
| `tools/Test-Translations.ps1` | Language file checks |
| `README.md`, `README.tr.md` | Documentation (English master + translations) |
| `docs/images/<language>/` | README screenshots, one folder per language |

The C# files are compiled at runtime with `Add-Type`; there is no separate build step and no dependencies.

## Development environment

- Windows 10 / 11, Windows PowerShell 5.1 (**not** PowerShell 7 — WPF and `Add-Type` behavior targets 5.1)
- Any text editor (VS Code recommended)

## Rules

### We're limited to C# 5

The `Add-Type` of Windows PowerShell 5.1 uses the .NET Framework compiler, which supports **C# 5**. Don't use:

- `$"..."` string interpolation → `string.Format` or `+`
- `?.` and `??=` → explicit `null` checks
- `nameof`, expression-bodied members (`=>`), auto-property initializers, `out var`, tuples
- Prefer plain loops over LINQ (so `System.Core` doesn't need to be referenced)

### No interface text in code

Every text the user sees comes from a language key:

- PowerShell: `T 'menu.sort'`, with values `T 'footer.apps' $count`
- C#: `Loc.T("reason.temp")`, `Loc.T("reason.lookalike", exe, name)`

New key names are lowercase and dotted (`area.name`, e.g. `detail.parent`). Add the key to **every**
`lang\*.json` file; you may use the English text for languages you don't speak.

### File encoding

- `NetGadget.ps1` and `tools\*.ps1` must be **UTF-8 with BOM**. Without the BOM, PowerShell 5.1 reads the file as ANSI
  and breaks non-ASCII characters.
- `lang\*.json` files are UTF-8.
- Line endings are handled by `.gitattributes`; don't change them by hand.

### Privacy principle

- The gadget **never sends** the data it monitors anywhere. Any feature that connects to the internet (e.g. reputation
  lookups) must be **off by default** and explicitly enabled from the menu.
- The gadget shouldn't create network traffic itself; if it does (like reverse DNS), it must be documented and
  possible to turn off.

### Suspicion score rules

New rules go into the `Evaluate` method in `Inspector.cs`. Every rule must:

- be selective enough **not to cause false alarms** on its own, or have a low score,
- give the user a reason explaining **why** it was flagged: `Reason(app, score, Loc.T("reason.newrule"))`,
  with the key added to all language files,
- be added to the score table in every README (`README.md`, `README.tr.md`).

## Adding a language

All interface texts live in `lang\<language code>.json` files.

1. Copy `lang\en.json` and name it after the two-letter language code (e.g. `lang\de.json`).
2. Set `language.name` to the language's own name (`Deutsch`) and `language.culture` to its culture code
   (`de-DE`). The culture code controls number and date formatting.
3. Translate the other values. **Don't touch the keys.** Keep the `{0}`, `{1}` placeholders; their order may change
   to fit the sentence.
4. Run the check; it lists missing, unused or placeholder-mismatched keys:

   ```powershell
   .\tools\Test-Translations.ps1
   ```

5. Try the gadget in the new language and look for text that doesn't fit:

   ```powershell
   .\NetGadget.ps1 -Snapshot C:\Temp\de.png -Language de
   ```

The new language shows up in the gadget's **Language / Dil** menu automatically, and is picked automatically when
it's the Windows display language. Untranslated keys fall back to English, so partial translations are useful too.

## Translating documentation

**English is the master version** of the documentation; translations use the same name with a language code:

| Document | Languages |
|---|---|
| `README.md` | English · [`README.tr.md`](README.tr.md) Turkish |
| `CONTRIBUTING.md` | English · [`CONTRIBUTING.tr.md`](CONTRIBUTING.tr.md) Turkish |
| `SECURITY.md`, `CHANGELOG.md` | Turkish only for now — English translations welcome |

- A new translation is added as `<NAME>.<language code>.md` (e.g. `README.de.md`).
- Every version starts with a language switcher line (`**English** · [Türkçe](README.tr.md)`). When you add a
  language, update this line in **all** language versions.
- Screenshots go into `docs/images/<language code>/` and are linked from the README in that language.
- When you change a document, update its other language versions too. If you can't translate, write
  "translation pending" in the pull request description; someone else can finish it.

## Testing

There's a test mode that renders the gadget off-screen and saves it as a PNG:

```powershell
.\NetGadget.ps1 -Snapshot C:\Temp\test.png
.\NetGadget.ps1 -Snapshot C:\Temp\test-tr.png -Language tr
```

- `-Snapshot` doesn't ask for administrator rights and doesn't touch your `known.tsv` / `trusted.tsv` files.
- `-Language` overrides the language setting (`en`, `tr`, `auto`).
- `-NoElevate` starts the gadget without asking for administrator rights (limited mode).
- Always test ETW-related changes **as administrator** too; the top right should show `live · TCP + UDP`.

Before opening a pull request:

- [ ] The gadget starts without errors both as administrator and with `-NoElevate`
- [ ] The `-Snapshot` image looks right in at least two languages (`-Language en`, `-Language tr`)
- [ ] Any new interface text exists in all `lang\*.json` files and `tools\Test-Translations.ps1` is clean
- [ ] If documentation changed, all language versions are updated (or marked "translation pending")
- [ ] `CHANGELOG.md` is updated if needed

## Commits and pull requests

- Commit messages can be in English or Turkish; keep the first line a short summary of what was done.
- Keep each pull request focused on one topic.
- Your contributions are published under the project's [MIT license](LICENSE).
