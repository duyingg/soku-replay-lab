# 第三方声明

## giuroll-hagb

`NativeCheckpoint.hpp` 的状态遍历参考了 giuroll-hagb 的 MIT 回滚结构遍历实现。

Copyright (c) 2023~2024 Giufin, 2024 Hagb (Junyu Guo) <hagb@hagb.name>

完整许可证保留于 `third_party/licenses/GIUROLL_LICENSE.txt`，其版权及许可声明继续适用。

## 游戏及图片

本项目的软件代码使用 MIT 许可证。游戏本体、游戏原始图片、音频、回放样本和其他游戏资源未包含在公开发行包中，也不由本项目的 MIT 许可证授予使用权。

`CardArt.zip` 是可选的本地图像归档，不随本包分发。缺少该文件不妨碍程序启动、采集和分析；卡牌原图不会显示。

## 运行与构建依赖

程序依赖 Microsoft .NET 9 Desktop Runtime（Windows WPF）。源码构建需要 .NET 9 SDK；原生探针及辅助程序还需要 Visual Studio C++ x86 工具链及 Windows SDK。上述依赖由使用者另行安装，未捆绑进本包，其各自许可条款继续适用。
