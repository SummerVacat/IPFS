# 第三方组件与许可说明

本程序「IPFS 媒体库」（程序集 `IPFS.Avalonia`，版本 0.2.0）整体以 **GNU General Public License v3.0 或更高版本（GPL-3.0-or-later）** 发布，许可证全文见根目录 `LICENSE`，也可从 <https://www.gnu.org/licenses/gpl-3.0.html> 获取。

本程序使用了以下第三方组件。各组件的版权归其各自作者所有，本程序仅在遵守其许可条款的前提下使用。

## 组件清单

| 组件 | 版本 | 许可证 | 获取地址 |
|---|---|---|---|
| Avalonia | 12.1.4 | MIT | https://www.nuget.org/packages/Avalonia |
| Avalonia.Desktop | 12.1.4 | MIT | https://www.nuget.org/packages/Avalonia.Desktop |
| Avalonia.Themes.Fluent | 12.1.4 | MIT | https://www.nuget.org/packages/Avalonia.Themes.Fluent |
| Avalonia.Fonts.Inter | 12.1.4 | MIT | https://www.nuget.org/packages/Avalonia.Fonts.Inter |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://www.nuget.org/packages/CommunityToolkit.Mvvm |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later | https://www.nuget.org/packages/LibVLCSharp |
| LibVLCSharp.Avalonia | 3.10.2 | LGPL-2.1-or-later | https://www.nuget.org/packages/LibVLCSharp.Avalonia |
| VideoLAN.LibVLC.Windows（VLC 3.0.24 原生库与插件） | 3.0.24 | LGPL-2.1-or-later（部分插件为 GPLv2-or-later） | https://www.nuget.org/packages/VideoLAN.LibVLC.Windows |
| SixLabors.ImageSharp | 3.1.12 | Six Labors Split License 1.0（本项目按 Apache-2.0 条款使用） | https://www.nuget.org/packages/SixLabors.ImageSharp |
| sqlite-net-pcl | 1.11.285 | MIT | https://www.nuget.org/packages/sqlite-net-pcl |
| SQLitePCLRaw.core | 3.0.3 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.core |
| SQLitePCLRaw.provider.e_sqlite3 | 3.0.3 | Apache-2.0 | https://www.nuget.org/packages/SQLitePCLRaw.provider.e_sqlite3 |
| SkiaSharp | 3.119.4 | MIT | https://www.nuget.org/packages/SkiaSharp |
| HarfBuzzSharp | 8.3.1.3 | MIT | https://www.nuget.org/packages/HarfBuzzSharp |
| Microsoft .NET 8 运行时 | 8.0 | MIT | https://github.com/dotnet/runtime |

## 许可证全文

- GPL-3.0：`LICENSE`、`licenses/GPL-3.0.txt`
- LGPL-2.1：`licenses/LGPL-2.1.txt`
- Apache-2.0：`licenses/Apache-2.0.txt`
- Six Labors Split License 1.0：`licenses/SixLabors-Split-License-1.0.txt`

## libvlc（LGPL-2.1-or-later）合规说明

1. 本程序以**未修改的独立动态链接库**形式分发 libvlc：`libvlc/win-x64` 目录下的 `libvlc.dll`、`libvlccore.dll` 与 `plugins` 目录。未对 VLC 原生代码做任何修改。
2. 用户可以自行替换上述动态库文件，以使用修改后的 libvlc 版本；本程序不阻止或限制此类替换。
3. libvlc 与 VLC 插件均以独立文件形式随包提供，不进行静态链接或单文件封装。
4. VLC 3.0.24 对应源码可从 <https://download.videolan.org/pub/videolan/vlc/3.0.24/> 获取。
5. LibVLCSharp 源码可从 <https://code.videolan.org/videolan/LibVLCSharp> 获取。
6. VLC 插件中包含部分 GPLv2-or-later 模块；本程序整体已按 GPL-3.0-or-later 发布，与该类模块兼容。

## Six Labors ImageSharp 说明

ImageSharp 3.1.12 采用 Six Labors Split License 1.0。本程序以 GPL-3.0-or-later 开源发布，属于该许可中「以开源或源码可见许可证发布的软件」情形，因此按 Apache License 2.0 条款使用，无需商业授权。若后续改变发布形态为闭源商业分发，需重新评估该组件的商业授权要求。

## MIT 许可证文本

以下 MIT 许可条款适用于上表中标记为 MIT 的组件，各组件版权归其各自版权持有人所有：

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
