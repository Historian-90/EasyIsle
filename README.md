# EasyIsle

A Dalamud plugin for Island Sanctuary that plans your Isleworks agenda from what your island
actually has: island rank, workshops, isleventory, crops, animals, granary expeditions,
Supply & Demand and Felicitous Favors.

EasyIsle only gives advice. It never sets your agenda or does anything for you; it reads what
your game already shows and runs entirely on your own machine.

## Install

1. In game, open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, paste this link, tick **Enabled**, and click **Save**:
   ```
   https://raw.githubusercontent.com/Historian-90/EasyIsle/main/repo.json
   ```
3. Open `/xlplugins`, search for **EasyIsle**, and install it.
4. Type `/ei` (or `/easyisle`) to open it.

## Status

Early development. This version shows everything EasyIsle can read about your island so each
piece can be checked in game. The planner comes next.

Some data only loads after you've opened the matching game window once per session:

- **Supply & Demand, rest days, groove:** open the Isleworks agenda.
- **Isleventory counts:** open your Isleventory (EasyIsle remembers the last counts it saw).
- **Felicitous Favors:** open the Felicitous Favors window.
- **Granary, cropland, pasture:** be on your island.
