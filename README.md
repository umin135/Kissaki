# Kissaki Asset Viewer

A reverse-engineering and asset preview tool for games built on Koei Tecmo's **Katana Engine**.

Currently tested against **Dead or Alive 6 Last Round**. Raw asset export is supported; more features are in active development.

Have questions or want to contribute to our research? Join the [Discord server](https://discord.gg/jwaB8zhb9v).  
Looking for a Mod Manager for Katana Engine games? → [Kashira Mod Manager](https://github.com/umin135/KashiraModManager)

## Features

- **Steam auto-detection** — detects supported Katana Engine games from your Steam library automatically
- **Mod-safe loading** — always reads original game files regardless of installed mods; fully compatible with Kashira Mod Manager
- **Asset preview** — resolves G1T/G1M texture and model references and renders them inline
- **Bundle export** — extracts all related assets for a given model in one click *(work in progress)*

## Requirements

- Windows 10 / 11 (x64)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) or later (.NET 9 / 10 are also supported)

## Known Limitations

- Mesh export (OBJ / glTF) is not yet implemented — only raw asset extraction is currently supported
- Bundle export is functional but incomplete for some asset types

## Disclaimer

Kissaki is developed strictly for **modding and personal research purposes**.  
You are solely responsible for any use of extracted assets. Redistribution of game assets is strictly prohibited.

## Credits

Sincere thanks to the community researchers and contributors whose work made this project possible.

- DeathChaos25 ([fdata_dump](https://github.com/DeathChaos25/fdata_dump))
- Joschuka ([Project-G1M](https://github.com/Joschuka/Project-G1M))
- MrIkso ([RDBExplorer](https://github.com/MrIkso/RDBExplorer))
- eterniti ([eternity_common](https://github.com/eterniti/eternity_common/tree/master/DOA6))
- yretenai ([Cethleann](https://github.com/yretenai/Cethleann))
- neptuwunium ([Cethleann fork](https://github.com/neptuwunium/Cethleann))
- kassent ([Nioh3-Model-Texture-Mapping-Database](https://github.com/kassent/Nioh3-Model-Texture-Mapping-Database))
- hearhellacopters ([G1T](https://github.com/hearhellacopters/G1T))
- bnnm ([vgm-tools](https://github.com/bnnm/vgm-tools))
- vgmstream ([vgmstream](https://github.com/vgmstream/vgmstream))
- eArmada8 ([gust_stuff](https://github.com/eArmada8/gust_stuff))
- VitaSmith ([gust_tools](https://github.com/VitaSmith/gust_tools))
- Ploaj ([Metanoia](https://github.com/Ploaj/Metanoia))
- DarkStarSword ([3d-fixes](https://github.com/DarkStarSword/3d-fixes))
- Thealexbarney ([VGAudio](https://github.com/Thealexbarney/VGAudio))
- SlowpokeVG ([DSP-to-KTSS-converter](https://github.com/SlowpokeVG/DSP-to-KTSS-converter))
- Hairo ([kvs-tools](https://github.com/Hairo/kvs-tools))
- lehieugch68 ([G1N-Font-Editor](https://github.com/lehieugch68/G1N-Font-Editor))
- TekkaGB ([P5SFontEditor](https://github.com/TekkaGB/P5SFontEditor))
- Nominom ([BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET))
- therathatter ([GitHub](https://github.com/therathatter))
- Jupisoft111 ([GitHub](https://github.com/Jupisoft111))
- Luigi Auriemma ([aluigi.altervista.org](https://aluigi.altervista.org/))
- Semory / Howfie ([Gas Machine](https://www.xnalara.org/viewtopic.php?t=1001))
- vagonumero13 (SRSXtool)
- ak2yny
- PredatorCZ
- Delguoqing
- Charsles
- ThatGamer
- RobCat030

## License

Kissaki is licensed under [GPL-3](LICENSE).
