<h1 align="center">DL Satchel</h1>

<p align="center">
<b>A simple save editor for DDV.</b><br>
Add items, clothes, furniture, companions and coins to your save on PC.
</p>

<p align="center">
<a href="https://github.com/Dysprohol/DLSatchel/releases/latest"><img alt="release" src="https://img.shields.io/github/v/release/Dysprohol/DLSatchel?label=release&cacheSeconds=300"></a>
<a href="https://github.com/Dysprohol/DLSatchel/releases"><img alt="downloads" src="https://img.shields.io/github/downloads/Dysprohol/DLSatchel/total?label=downloads&cacheSeconds=300"></a>
<a href="LICENSE"><img alt="license" src="https://img.shields.io/github/license/Dysprohol/DLSatchel?label=license"></a>
<a href="https://ko-fi.com/Dysprohol"><img alt="Ko-fi" src="https://img.shields.io/badge/Ko--fi-support-FF5E5B?logo=ko-fi&logoColor=white"></a>
</p>

![The editor with a save open](docs/img/screen-overview.png?v=1.4.1)

Your whole game is kept in one file on your PC. DL Satchel opens that file and shows it as simple
lists. You pick what you want, click **Save Changes**, start the game - and it is there.

You don't need to know anything about files. The save is encrypted by the game; the editor
handles that by itself. Before every save it makes a backup, so you can always go back.

> Fan-made and unofficial. Not made by or affiliated with the game's developer or publisher. PC version only. Changing a save is at your own risk.

## What you need

1. **DL Satchel** - download `DLSatchel.exe` from
   [Releases](https://github.com/Dysprohol/DLSatchel/releases). It is one file, nothing to install.
2. **Two item lists** - the editor does **not** work without them. Players on the FearLess
   Revolution forum keep them up to date, and the editor downloads them for you:
   - [KOSDDV item dump](https://docs.google.com/spreadsheets/d/1uxiBlhMhTtuo7xMd5gGsj7H7GyowfOlJqfA-R_i5jjM/edit)
   - [Rubyelf's ID list](https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/edit)

   New items and questions: [the forum thread](https://fearlessrevolution.com/viewtopic.php?f=2&t=21330),
   where the people who make the lists post.

## How to use it

1. **Close the game.**
2. Start `DLSatchel.exe`. If Windows warns you, click **More info > Run anyway**.
3. The first time, the editor asks to download the item lists - click **Yes**. That's all; it
   remembers them. (Already have the Excel files? Click **Item Catalog** and **+ Add Catalog** and
   pick them instead.)
4. Click **Find Game Save** and choose your save.
5. Type a name in **Item name**, click the item in the list, set **Amount** and click **Add item**
   (or just double click the item). The editor puts it in the right place by itself.
   To add several at once, select them with **Ctrl** or **Shift** and click **Add item**.
   Companions are added the same way and show up in the game's Companions menu.

   ![Finding and adding an item](docs/img/screen-add-item.png?v=1.4.1)

6. To remove something, open a collection on the right (for example **Furniture**), select it and
   click **Remove selected**. Coins and level are at the top left.

   ![Your furniture](docs/img/screen-collection.png?v=1.4.1)

7. Click **Save Changes**. The editor shows what will change (for example "Backpack") and asks you
   to confirm. Then start the game.

## Umbrellas and parasols

Things your character holds - umbrellas, parasols and the like - are **not clothes**. The game keeps
them together with your tools. Open the **Tools & Accessories** tab, find the item and click
**Add item**. It is added unequipped, so you pick it in the game.

![Tools and accessories](docs/img/screen-tools.png?v=1.4.1)

## Decoration presets

Ready-made decoration sets (for example "Knight Castle Preset" or "Haunted Mansion Wall
Preset") are on the **Presets** tab. Pick one and click **Add item** - after saving, it shows up in
the game's preset menu.

A preset can only be placed if you own its furniture. When you add one, the editor shows which
pieces you are missing and can add them to your furniture storage in one go.

Most presets come from the shop. The ones the lists call "unreleased" are hidden at first: untick
**Hide risky or unfinished items** to see them.

![Decoration presets](docs/img/screen-presets-furniture.png?v=1.4.1)

## Adding everything you're missing

Open a collection on the right (for example **Wardrobe**) and click **Add all missing…**. The editor
finds every item of that collection you don't have yet and lets you choose which kinds to add.

Only ordinary items and event items are ticked at first. Everything else stays unticked until you
tick it: quest rewards, shop items, expansion or DLC items, styles for houses and characters, and
items the community list hasn't checked yet (new, unfinished or developer items from the game
files). Things already placed in your valley count as yours, so you don't get them twice. Items
marked unsafe, unreleased or test, and technical game objects, are never added.

Nothing here is guaranteed. The lists are made by players and can be wrong, especially right after
a game update. **Back up your save, add a little, check in the game, then add more.**

![Adding everything you're missing](docs/img/screen-add-missing.png?v=1.5.0)

## Mermaid parts and ships

The latest game update added mermaid tails, fins and ears, and ship skins. They go to your wardrobe and
skins like any other item, but you can wear and use them only after the game unlocks the mermaid
form and the ship in its story. They are marked **CAUTION**, and until your save has the mermaid form
or the ship, the editor warns you when you add them.

## When the game updates

New items show up in the editor once the people who make the lists add them. When your lists are
more than a week old, a gold line at the bottom says so - click **Update now**. **Check Save** offers
the same when your save has items the lists don't know yet. When the game itself was updated after
your lists, the gold line says so too: new items are not checked by anyone yet, so wait a few days
or add carefully.

![Your item lists are 18 days old. Update now](docs/img/screen-lists-notice.png?v=1.4.1)

A list is only replaced if the new copy reads correctly, so a broken or emptied sheet can't spoil
anything. Fresh copies are kept in the editor's own folder; your own Excel files are never changed.

If a list stops working (the sheet was moved, deleted or made private), the editor says so in plain
words and keeps using the list you have. You can paste the list's new address, choose an Excel file
you downloaded yourself, or open the forum thread where the list makers post news.

![A list that stopped working](docs/img/screen-list-problem.png?v=1.4.1)

Everything about the lists is also in **Sources…**, including where they come from: if a list moves
to a new address, paste the new link in the **Link** column; **Clean up…** takes switched-off lists
off the list and moves old copies the editor kept to the Recycle Bin.

## What the labels mean

Every item in the list has a label in the **Safety** column. The labels only repeat what the lists
say - they are not a guarantee:

| Label | What it means |
|---|---|
| **NO WARNINGS** | The lists have no warnings about it. Still back up first. |
| **CAUTION** (brown row) | A quest, event or shop item. Added too early, it can get a quest stuck. |
| **UNSAFE** | Broken or a test item that can damage a save. Hidden unless you turn it on. |
| **UNKNOWN** | The lists say too little about it. |

## Updates

Top right in the editor, click **Check for updates**. If there is a newer version, click
**Update now** - the editor downloads it, checks it and restarts. Tick **Check at start** to check
every time. The editor only goes online when you click something (updates or item lists).

## Going back

Next to your save the editor keeps two kinds of copies:

- `profile.json.bak` - your save from before the first change,
- `profile.json.backup-<date>` - a copy from before every save.

To go back: close the game and rename the copy you want to `profile.json`. The copies are in the
same folder as your save - the game keeps it under `%USERPROFILE%\AppData\LocalLow` (paste that into
File Explorer and open the game's folder).

## Support

Did it save you some grinding?

<a href="https://ko-fi.com/Dysprohol"><img alt="Support me on Ko-fi" src="https://ko-fi.com/img/githubbutton_sm.svg"></a>

## License

This project is licensed under the [MIT License](LICENSE).

<details>
<summary>For developers</summary>

.NET 10 WPF. `src/DLSatchel` is the app, `tests/DLSatchel.Tests` the tests.

```bash
dotnet build DLSatchel.sln
dotnet test tests/DLSatchel.Tests
