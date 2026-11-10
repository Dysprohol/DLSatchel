<h1 align="center">DL Satchel</h1>

<p align="center">
<b>A simple save editor for Disney Dreamlight Valley.</b><br>
Add items, clothes, furniture, companions and coins to your save on PC.
</p>

![The editor with a save open](docs/img/screen-overview.png?v=1.3.1)

Your whole game is kept in one file on your PC. DL Satchel opens that file and shows it as simple
lists. You pick what you want, click **Save Changes**, start the game - and it is there.

You don't need to know anything about files. The save is encrypted by the game; the editor
handles that by itself. Before every save it makes a backup, so you can always go back.

> Fan-made and unofficial. Not affiliated with Gameloft or Disney. PC version only. Changing a save is at your own risk.

## What you need

1. **DL Satchel** - download `DLSatchel.exe` from
   [Releases](https://github.com/Dysprohol/DLSatchel/releases). It is one file, nothing to install.
2. **Two item lists** - the editor does **not** work without them. Players on the FearLess
   Revolution forum keep them up to date, and the editor downloads them for you (step 3):
   - [KOSDDV item dump](https://docs.google.com/spreadsheets/d/1uxiBlhMhTtuo7xMd5gGsj7H7GyowfOlJqfA-R_i5jjM/edit)
   - [Rubyelf's ID list](https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/edit)

   New items and questions: [the forum thread](https://fearlessrevolution.com/viewtopic.php?f=2&t=21330),
   where the people who make the lists post.

## How to use it

1. **Close the game.**
2. Start `DLSatchel.exe`. If Windows warns you, click **More info > Run anyway**.
3. Click **Sources…**, then **+ Add from link…**. Click **Rubyelf ID list** and **Download and add**.
   Do the same with **KOSDDV item dump**. You only do this once - the editor remembers them.
   (Already have the Excel files? Click **Item Catalog** and **+ Add Catalog** and pick them instead.)

   ![Adding a list from its link](docs/img/screen-add-link.png?v=1.3.1)

4. Click **Find Game Save** and choose your save.
5. Type a name in **Item name**, click the item in the list, set **Amount** and click **Add item**
   (or just double click the item). The editor puts it in the right place by itself.

   ![Finding and adding an item](docs/img/screen-add-item.png?v=1.3.1)

6. To remove something, open a collection on the right (for example **Furniture**), select it and
   click **Remove selected**. Coins and level are at the top left.

   ![Your furniture](docs/img/screen-collection.png?v=1.3.1)

7. Click **Save Changes**. The editor shows what will change (for example "Backpack") and asks you
   to confirm. Then start the game.

## Umbrellas and parasols

Things your character holds - umbrellas, parasols and the like - are **not clothes**. The game keeps
them together with your tools. Open the **Tools & Accessories** tab, find the item and click
**Add item**. It is added unequipped, so you pick it in the game.

![Tools and accessories](docs/img/screen-tools.png?v=1.3.1)

## Decoration presets

Gameloft's ready-made decoration sets (for example "Knight Castle Preset" or "Haunted Mansion Wall
Preset") are on the **Presets** tab. Pick one and click **Add item** - after saving, it shows up in
the game's preset menu.

A preset can only be placed if you own its furniture. When you add one, the editor shows which
pieces you are missing and can add them to your furniture storage in one go.

Most presets come from the shop. The ones the lists call "unreleased" are hidden at first: untick
**Hide risky or unfinished items** to see them.

![Decoration presets](docs/img/screen-presets-furniture.png?v=1.3.1)

## When the game updates

New items show up in the editor once the people who make the lists add them. Click **Sources…** and
**Update from links** to download fresh lists. A list is only replaced if the new copy reads
correctly, so a broken or emptied sheet can't spoil anything. If a list moves to a new address,
paste the new link in the **Link** column.

**Check Save** tells you if your save has items the lists don't know yet.

![Item lists with their links](docs/img/screen-sources.png?v=1.3.1)

## What the labels mean

Every item in the list has a label in the **Safety** column:

| Label | What it means |
|---|---|
| **SAFE** | A normal item. |
| **CAUTION** (brown row) | A quest, event or shop item. Added too early, it can get a quest stuck. |
| **UNSAFE** | Broken or a test item that can damage a save. Hidden unless you turn it on. |
| **UNKNOWN** | The lists say too little about it. |

## Updates

Top right in the editor, click **Check for updates**. If there is a newer version, click
**Update now** - the editor downloads it, checks it and restarts. Tick **Check at start** to check
every time; otherwise the editor never goes online by itself.

## Going back

Next to your save the editor keeps two kinds of copies:

- `profile.json.bak` - your save from before the first change,
- `profile.json.backup-<date>` - a copy from before every save.

To go back: close the game and rename the copy you want to `profile.json`. The save folder is
`%USERPROFILE%\AppData\LocalLow\Gameloft\Disney Dreamlight Valley` (paste it into File Explorer).

## Support

Did it save you some grinding? [Buy me a coffee on Ko-fi](https://ko-fi.com/Dysprohol).

## License

This project is licensed under the [MIT License](LICENSE).

<details>
<summary>For developers</summary>

.NET 10 WPF. `src/DLSatchel` is the app, `tests/DLSatchel.Tests` the tests.

```bash
dotnet build DLSatchel.sln
dotnet test tests/DLSatchel.Tests
