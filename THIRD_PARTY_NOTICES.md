# Third-party notices

## System Informer SDK

https://github.com/winsiderss/systeminformer

Public headers and an import library are generated from revision `bfc8145f2744a415319ccaaa8f1e32dd2cf1e596`. Upstream notices are retained; the package includes `licenses/SystemInformer.LICENSE.txt`. This plugin is a separate project and is not endorsed by System Informer.

## nlohmann/json

https://github.com/nlohmann/json — version 3.11.3. Copyright Niels Lohmann. MIT license in `vendor/json.LICENSE.MIT`.

## WSL Tools for System Informer

Installer and settings-file handling are adapted from WSL Tools. Copyright (c) 2026 David Trapp / Trapp Innovations, MIT. See `vendor/wsl-tools.LICENSE.MIT`.

## .NET runtime

https://github.com/dotnet/runtime

The capture helper includes Microsoft's self-contained .NET runtime. Its license and third-party notices are included with the helper payload.

## Optional downloaded tools

py-spy: https://github.com/benfred/py-spy — MIT, copyright Ben Frederickson and contributors. Its license is preserved from the downloaded wheel.

Eclipse Temurin OpenJDK: https://adoptium.net/ — GPLv2 with the Classpath Exception and associated third-party licenses. Downloaded JDK archives retain their full `legal`/license files. Corresponding source is available from the exact release listed in `helper/ToolInstaller.cs` on https://github.com/adoptium. These tools are downloaded only with the user's consent; they are not embedded in the base installer.
