# ACEO KTL Tweaks

A collection of gameplay and economy tweaks for **Airport CEO**, focused on improving balance, realism, and clarity in how the game handles costs, fines, and economic systems.

---

## ✨ Overview

This mod adjusts several core systems in Airport CEO to make them:

- More **transparent** (what you see = what you actually pay)
- More **balanced** (large airports scale differently than small ones)
- More **configurable** (multipliers control most behaviour)

It uses Harmony patches to safely modify existing game logic without replacing core files.

---

## ⚙️ Features

### 💸 Economy & Expenses
- Scales **vehicle operating costs** by category
- Fixes incorrect vanilla values (e.g. FireTruck base cost bug)
- Ensures UI always reflects actual costs correctly

---

### 🚨 Fines System Overhaul
- Global **fines multiplier** for all penalties
- Reworked **licence scaling**:
  - Small airports → lower fines
  - Large airports → significantly higher fines
- Ensures **emails and actual charges always match** (vanilla mismatch fixed)

---

### 🧾 Taxes
- Configurable **tax multiplier**
- Removes hidden CFO tax reduction (no more silent 0.75× modifier)
- Taxes now behave consistently and predictably

---

### 👔 Executive Modifiers (Removed Hidden Effects)
- Neutralises undocumented CFO bonuses:
  - ❌ Fine reductions
  - ❌ Tax reductions
- All scaling is now controlled by the mod’s own settings

---

### 🧯 Disaster & Incident Handling
- Fine scaling applies consistently to:
  - Incident resolution penalties
  - Security violations
- Fixes mismatch between:
  - Email reports
  - Actual deducted amounts

---

## 🎯 Design Philosophy

This mod aims to:

- Remove **hidden mechanics**
- Replace them with **explicit, configurable systems**
- Improve **progression scaling** (small → large airport feels meaningful)
- Keep everything **predictable and readable**

---

## 🛠️ Installation

1. Install **Harmony** (if not already included via your mod loader)
2. Place the mod files in your Airport CEO mods directory
3. Launch the game

> Exact steps may vary depending on your mod loader/setup.

---

## ⚙️ Configuration

Most behaviour is controlled via multipliers (defined in the mod config):

- `FinesMultiplier`
- `TaxMultiplier`
- Vehicle cost scaling
- Licence scaling rules

Tweak these to match your preferred difficulty and realism level.

---

## 🤝 Contributing

Contributions are **very welcome**.

If you want to:
- Improve balance
- Add new systems
- Refactor code
- Suggest ideas

Feel free to:
- Open an issue
- Submit a pull request
- Fork and experiment

---

## 📜 License

This project is licensed under the **MIT License**.

You are free to:
- Use
- Modify
- Distribute
- Include in other projects (even closed-source)

Just include proper attribution.

---

## ⚠️ Notes

- This mod uses Harmony patching and may conflict with other mods that modify:
  - EconomyController
  - IncidentController
  - Procurement systems
- Load order can matter if multiple mods target the same systems

---

## 🙌 Credits

- Developed as a community-driven tweak project  
- Built with experimentation, iteration, and a bit of “vibecoding”

---

## 📬 Feedback

If something feels off balance-wise or breaks:
- Open an issue
- Share your config + scenario

Balancing is ongoing 👍