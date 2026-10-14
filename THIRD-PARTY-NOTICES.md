# Third-party notices

## TagLibSharp 2.3.0
Used for reading and writing audio tags. Licensed under the GNU Lesser General Public License, version 2.1.
Source: https://github.com/mono/taglib-sharp

The library is used unmodified. When the plugin is built, the unmodified TagLibSharp.dll is embedded as a resource (planned for the plugin shell in Phase 3); the LGPL notice in this file applies to it.

`tools/TagLibSharp.dll` is the same unmodified TagLibSharp 2.3.0 binary (from the NuGet package, `lib/netstandard2.0`), included so `tools/Export-LibraryTags.ps1` runs on a machine without the .NET SDK. Source: https://github.com/mono/taglib-sharp
