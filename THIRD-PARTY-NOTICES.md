# Third-party notices

## TagLibSharp 2.3.0
Used for reading and writing audio tags. Licensed under the GNU Lesser General Public License, version 2.1.
Source: https://github.com/mono/taglib-sharp

The library is used unmodified. When the plugin is built, the unmodified TagLibSharp.dll (from the NuGet package, `lib/netstandard2.0`) is embedded as a resource of `LabelMaker.dll` and loaded from there at run time; the LGPL notice in this file applies to it. You may replace the embedded library with a modified build of TagLibSharp by rebuilding the plugin from source (`dotnet build src/LabelMaker -c Release`) with that package version changed.

`tools/TagLibSharp.dll` is the same unmodified TagLibSharp 2.3.0 binary (from the NuGet package, `lib/netstandard2.0`), included so `tools/Export-LibraryTags.ps1` runs on a machine without the .NET SDK. Source: https://github.com/mono/taglib-sharp
