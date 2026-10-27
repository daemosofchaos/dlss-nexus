# Third-party notices

DLSS Nexus's own source code is licensed under the [MIT License](LICENSE). Third-party components remain subject to their own licenses and distribution terms. The MIT license for Nexus and this document do not grant additional redistribution rights to third-party components. Public distribution requires the applicable permissions and notices for every included binary, shader and runtime.

## .NET 10 / WPF

The self-contained application includes Microsoft .NET runtime and Windows Desktop components. Corresponding license and third-party notices are retained in the licenses folder. See the [.NET runtime license](licenses/DotNet-MIT.txt), [.NET runtime third-party notices](licenses/DotNet-THIRD-PARTY-NOTICES.txt), [WPF license](licenses/WPF-LICENSE.txt) and [WPF third-party notices](licenses/WPF-THIRD-PARTY-NOTICES.txt).

## Inno Setup

The application installer is compiled using [Inno Setup](https://jrsoftware.org/). Its supplied [license](licenses/Inno-Setup.txt) is retained. The compiler is a development tool, not bundled with the application, and remains subject to its own licensing requirements.

## DLSS5 Feeder

[DLSS5 Feeder](https://github.com/jlrouzies-fr/DLSS5-Feeder) is created by **Jean-Laurent Rouzies (jlrouzies-fr)** and contributors and is MIT-licensed. Its supplied [MIT notice](licenses/DLSS5-Feeder-MIT.txt) also credits portions derived from **NIGos's [dlss5-dx11-bridge](https://github.com/NIGos/dlss5-dx11-bridge)**. Upstream and locally available dependency notices are retained in the licenses folder. Headers and embedded copyright notices in the payloads are not removed.

The DX10 helper add-on and host executable come from the same official Feeder 1.19.0 release and retain those upstream notices. Nexus does not modify those binaries.

## NVIDIA runtimes and RenoDX add-ons

NVIDIA DLSS trademarks and runtime components belong to their respective owners and remain subject to the applicable NVIDIA terms.

The default bundled `nvngx_dlssnr.dll` is **ShortFuse's patched FP16 DLSS-NR 310.9.16 compatibility build** for **GeForce RTX 20, RTX 30 and RTX 40 series GPUs**, released on **16 October 2026** under the tag `compat-fp16-310.9.16-cuda13.4.3`. ShortFuse edited and provided this runtime to add compatibility for those card generations. It is a modified, unsigned build of the NVIDIA runtime.

The selectable **DLSS-NR 310.8.0.0** alternative is an unmodified, NVIDIA-signed runtime. Its original bytes are retained under `versions/nr-310.8.0/`; it is not ShortFuse's patched FP16 build and the same GPU compatibility must not be assumed.

The Standard `renodx-dlss.addon64` is supplied by **ShortFuse (clshortfuse)** through [RenoDX](https://github.com/clshortfuse/renodx), with credit to the RenoDX contributors for the underlying framework. The legacy Generic `renodx-dlss5.addon64` build is credited to **Krish** and the [RenoDX community](https://discord.gg/F6AUTeWJHM).

The previous **ShortFuse 0.2026.1003.2333** alternative retains its original bytes under `versions/shortfuse-20261003/`. Although supplied as `renodx-dlss5.addon64`, its internal identity is ShortFuse/Controller, not Generic; Nexus deploys it as `renodx-dlss.addon64`. It retains the same creator attribution and applicable distribution terms. Both selectable alternatives are included starting with Nexus 1.1.0.

DX10's default Generic v7.0.0-rc8 is supplied through **[RHI / RankFTW](https://github.com/RankFTW/rhi-repo/releases/tag/renodx-dlss5-7.0.0-rc8)**, the distribution provider referenced by Feeder's installer. Credit for the add-on remains with Krish and the RenoDX contributors; the provider's download does not confer additional distribution rights.

These supplied binaries retain their original ownership and distribution terms. Local possession and a matching checksum are not proof of redistribution permission. Public packages remain subject to the relevant author permissions and license-notice requirements.

## Shader assets

The included [LumeniteFX](https://github.com/umar-afzaal/LumeniteFX) kernel and shader assets are by **Umar Afzaal / Afzaal (Kaidō)**. Their headers identify Copyright (c) Afzaal and the [AGNYA License](https://github.com/nvb-uy/AGNYA-License). Their attribution and license references remain in the installed shaders. Redistribution is subject to that license.

ReShade.fxh and ReShadeUI.fxh retain their source license headers. DrawText.fxh retains its kingreic1992 attribution. Texture and include assets retain the conditions of their original distribution. Do not assume that this notice supersedes or satisfies every shader asset's distribution requirements.

## ReShade

DLSS Nexus downloads the official full add-on ReShade setup from [reshade.me](https://reshade.me/) when a game needs ReShade. It caches the download and installs the unmodified x64 DLL; it does not run the upstream setup program. The ReShade proxy and setup are not bundled with Nexus. ReShade is BSD-3-Clause licensed, Copyright (C) Patrick Mours; see [ReShade-LICENSE.md](licenses/ReShade-LICENSE.md) and the [official source](https://github.com/crosire/reshade). Full add-on support is intended for suitable single-player games and may cause bans in multiplayer games.

This software is not affiliated with or endorsed by NVIDIA, Microsoft, ReShade or the third-party add-on authors.
