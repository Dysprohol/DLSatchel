<h1 align="center">DL Satchel</h1>

<p align="center">
<b>Prosty edytor zapisu.</b><br>
Dodawaj przedmioty, ubrania, meble, towarzyszy i monety do swojego zapisu na PC.<br>
<a href="README.md">English version</a>
</p>

![Edytor z otwartym zapisem](docs/img/screen-overview.png?v=1.3.1)

Cała Twoja gra siedzi w jednym pliku na komputerze. DL Satchel otwiera ten plik i pokazuje go jako
zwykłe listy. Wybierasz, co chcesz, klikasz **Save Changes**, uruchamiasz grę - i to już tam jest.

Nie musisz znać się na plikach. Gra szyfruje zapis, a edytor sam sobie z tym radzi. Przed każdym
zapisem robi kopię zapasową, więc zawsze możesz wrócić.

> Nieoficjalne, zrobione przez fana. Tylko wersja na PC. Zmiana zapisu na własne ryzyko.

## Czego potrzebujesz

1. **DL Satchel** - pobierz `DLSatchel.exe` z
   [Releases](https://github.com/Dysprohol/DLSatchel/releases). To jeden plik, nic się nie instaluje.
2. **Dwóch list przedmiotów** - **bez nich edytor nie działa**. Prowadzą je gracze z forum
   FearLess Revolution, a edytor sam je pobiera (krok 3):
   - [dump przedmiotów KOSDDV](https://docs.google.com/spreadsheets/d/1uxiBlhMhTtuo7xMd5gGsj7H7GyowfOlJqfA-R_i5jjM/edit)
   - [lista ID od Rubyelf](https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/edit)

   Nowe przedmioty i pytania: [wątek na forum](https://fearlessrevolution.com/viewtopic.php?f=2&t=21330),
   gdzie piszą autorzy list.

## Jak używać

1. **Zamknij grę.**
2. Uruchom `DLSatchel.exe`. Jeśli Windows ostrzega, kliknij **Więcej informacji > Uruchom mimo to**.
3. Kliknij **Sources…**, a potem **+ Add from link…**. Kliknij **Rubyelf ID list** i **Download and add**.
   To samo zrób z **KOSDDV item dump**. Robisz to tylko raz - edytor je zapamiętuje.
   (Masz już pliki Excel? Kliknij **Item Catalog** i **+ Add Catalog** i wybierz je.)

   ![Dodawanie listy z linku](docs/img/screen-add-link.png?v=1.3.1)

4. Kliknij **Find Game Save** i wybierz swój zapis.
5. Wpisz nazwę w **Item name**, kliknij przedmiot na liście, ustaw **Amount** i kliknij **Add item**
   (albo po prostu kliknij przedmiot dwa razy). Edytor sam włoży go we właściwe miejsce.

   ![Wyszukiwanie i dodawanie przedmiotu](docs/img/screen-add-item.png?v=1.3.1)

6. Żeby coś usunąć, otwórz kolekcję po prawej (np. **Furniture**), zaznacz przedmiot i kliknij
   **Remove selected**. Monety i poziom są na górze po lewej.

   ![Twoje meble](docs/img/screen-collection.png?v=1.3.1)

7. Kliknij **Save Changes**. Edytor pokaże, co się zmieni (np. „Backpack”), i poprosi
   o potwierdzenie. Potem uruchom grę.

## Parasolki i parasole

Rzeczy, które postać trzyma w ręce - parasolki, parasole i podobne - to **nie ubrania**. Gra trzyma je
razem z narzędziami. Otwórz zakładkę **Tools & Accessories**, znajdź przedmiot i kliknij
**Add item**. Zostanie dodany jako nieużywany, więc wybierzesz go w grze.

![Narzędzia i akcesoria](docs/img/screen-tools.png?v=1.3.1)

## Presety dekoracji

Gotowe zestawy dekoracji od Gameloftu (np. „Knight Castle Preset” albo „Haunted Mansion Wall
Preset”) są w zakładce **Presets**. Wybierz jeden i kliknij **Add item** - po zapisaniu pojawi się
w grze w menu presetów.

Preset da się postawić tylko wtedy, gdy masz jego meble. Przy dodawaniu edytor pokaże, których
brakuje, i może je od razu dodać do Twojego magazynu mebli.

Większość presetów pochodzi ze sklepu. Te, które listy opisują jako „unreleased”, są na początku
ukryte: odznacz **Hide risky or unfinished items**, żeby je zobaczyć.

![Presety dekoracji](docs/img/screen-presets-furniture.png?v=1.3.1)

## Po aktualizacji gry

Nowe przedmioty pojawiają się w edytorze, gdy autorzy list je dopiszą. Kliknij **Sources…**
i **Update from links**, żeby pobrać świeże listy. Lista zostaje podmieniona tylko wtedy, gdy nowa
kopia wczyta się poprawnie, więc zepsuty albo wyczyszczony arkusz niczego nie popsuje. Jeśli lista
zmieni adres, wklej nowy link w kolumnie **Link**.

**Check Save** powie Ci, czy w zapisie są przedmioty, których listy jeszcze nie znają.

![Listy przedmiotów z linkami](docs/img/screen-sources.png?v=1.3.1)

## Co znaczą oznaczenia

Każdy przedmiot na liście ma oznaczenie w kolumnie **Safety**:

| Oznaczenie | Co znaczy |
|---|---|
| **SAFE** | Zwykły przedmiot. |
| **CAUTION** (brązowy wiersz) | Z questa, eventu albo sklepu. Dodany za wcześnie może zablokować quest. |
| **UNSAFE** | Zepsuty albo testowy, może uszkodzić zapis. Ukryty, dopóki go nie włączysz. |
| **UNKNOWN** | Listy mówią o nim za mało. |

## Aktualizacje

W prawym górnym rogu edytora kliknij **Check for updates**. Jeśli jest nowsza wersja, kliknij
**Update now** - edytor ją pobierze, sprawdzi i uruchomi się ponownie. Zaznacz **Check at start**,
żeby sprawdzał przy każdym uruchomieniu; inaczej sam z siebie nie łączy się z internetem.

## Powrót do poprzedniego stanu

Obok zapisu edytor trzyma dwa rodzaje kopii:

- `profile.json.bak` - zapis sprzed pierwszej zmiany,
- `profile.json.backup-<data>` - kopia sprzed każdego zapisu.

Żeby wrócić: zamknij grę i zmień nazwę wybranej kopii na `profile.json`. Folder zapisu to
`%USERPROFILE%\AppData\LocalLow\Gameloft\Disney Dreamlight Valley` (wklej w Eksplorator plików).

## Wsparcie

Oszczędził Ci trochę grindu? [Postaw mi kawę na Ko-fi](https://ko-fi.com/Dysprohol).

<details>
<summary>Dla programistów</summary>

.NET 10 WPF. `src/DLSatchel` to aplikacja, `tests/DLSatchel.Tests` to testy.

```
dotnet build DLSatchel.sln
dotnet test tests/DLSatchel.Tests
```

Opublikuj `src/DLSatchel` profilem `FolderProfile`, a jednoplikowy `.exe` trafi do `output\`.
Część testów potrzebuje obu plików Excel w folderze `pliki_testowe` obok solucji.

</details>
