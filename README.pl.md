<h1 align="center">DL Satchel</h1>

<p align="center">
<b>Prosty edytor zapisu.</b><br>
Dodawaj przedmioty, ubrania, meble, towarzyszy i monety do swojego zapisu na PC.<br>
<a href="README.md">English version</a>
</p>

![Edytor z otwartym zapisem](docs/img/dl-satchel-overview.png)

Cała Twoja gra siedzi w jednym pliku na komputerze. Ten program pokazuje go jako zwykłe listy:
wybierasz, co dodać albo usunąć, klikasz **Save Changes**, uruchamiasz grę - gotowe. Za każdym
razem robi kopię zapasową, więc zawsze możesz wrócić.

Gra trzyma zapis **zaszyfrowany**. Edytor sam go odszyfrowuje i przy zapisie szyfruje z powrotem,
więc nie potrzebujesz żadnych innych narzędzi.

> Nieoficjalne, zrobione przez fana. Tylko wersja na PC. Zmiana zapisu na własne ryzyko.

## Potrzebujesz dwóch rzeczy

1. **Edytora** - pobierz `DLSatchel.exe` z
   [Releases](https://github.com/Dysprohol/DLSatchel/releases). Jeden plik, nic się nie
   instaluje.
2. **List przedmiotów (pliki Excel)** - **bez nich edytor nie działa**. Tworzą je gracze na forum
   FearLess Revolution:
   - **Dump przedmiotów KOSDDV** - otwórz [ten arkusz Google](https://docs.google.com/spreadsheets/d/1uxiBlhMhTtuo7xMd5gGsj7H7GyowfOlJqfA-R_i5jjM/edit)
     i kliknij **Plik > Pobierz > Microsoft Excel (.xlsx)**.
   - **Lista ID od Rubyelf** - otwórz [ten arkusz Google](https://docs.google.com/spreadsheets/d/1bBrfqq6m9qqZDXFVtqf646xD5LvLM2W1q1YTH0YVoZo/edit)
     i kliknij **Plik > Pobierz > Microsoft Excel (.xlsx)**.

   Najlepiej użyć obu. Nowe przedmioty i pytania:
   [wątek na forum](https://fearlessrevolution.com/viewtopic.php?f=2&t=21330),
   gdzie piszą autorzy list.

## Jak używać

1. **Zamknij grę.**
2. Uruchom edytor (jeśli Windows ostrzega: **Więcej informacji > Uruchom mimo to**).
3. Kliknij **Item Catalog** i wybierz plik Excel; drugi dodaj przyciskiem **+ Add Catalog**.
   Robisz to tylko raz.
4. Kliknij **Find Game Save** i wybierz swój zapis.
5. Znajdź przedmiot: wpisz nazwę w **Item name**, kliknij go na liście, ustaw **Amount**,
   kliknij **Add item**. Sam trafi we właściwe miejsce (plecak, szafa, meble...).
   Parasolki, parasole i inne rzeczy trzymane w ręce to nie ubrania: znajdziesz je w zakładce
   **Tools & Accessories**.

   ![Wyszukiwanie i dodawanie przedmiotu](docs/img/dl-satchel-add-item.png)

6. Żeby coś usunąć, otwórz kolekcję po prawej, zaznacz i kliknij **Remove selected**.
   Monety i poziom zmieniasz na górze po lewej.
7. Kliknij **Save Changes**, potwierdź i uruchom grę.

![Twoje kolekcje](docs/img/dl-satchel-collection.png)

## Aktualizacje

W prawym górnym rogu edytora: **GitHub** otwiera tę stronę, **Check for updates** sprawdza na
GitHubie, czy jest nowsza wersja. Jeśli jest, kliknij **Update now** - edytor ją pobierze, sprawdzi
i uruchomi się ponownie. Zaznacz **Check at start**, żeby sprawdzał przy każdym uruchomieniu;
inaczej sam z siebie nie łączy się z internetem.

## Oznaczenia bezpieczeństwa

| | |
|---|---|
| **SAFE** | Zwykły przedmiot. |
| **CAUTION** (brązowy wiersz) | Z questa, eventu albo sklepu. Dodany za wcześnie może zablokować quest. |
| **UNSAFE** | Zepsuty albo testowy, może uszkodzić zapis. Ukryty, dopóki go nie włączysz. |
| **UNKNOWN** | Listy mówią o nim za mało. |

## Powrót do poprzedniego stanu

Obok zapisu edytor trzyma `profile.json.bak` (sprzed pierwszej zmiany) i
`profile.json.backup-<data>` (sprzed każdego zapisu). Zamknij grę, zmień nazwę wybranej kopii
na `profile.json` i gotowe. Folder zapisu:
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

Opublikuj `src/DLSatchel` profilem `FolderProfile`, a jednoplikowy `.exe` trafi do
`output\`. Część testów potrzebuje obu plików Excel w folderze `pliki_testowe` obok solucji.

</details>
