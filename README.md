# 手柄键鼠映射

面向 Windows 11 x64 的手柄键盘/鼠标映射工具。使用 Microsoft GameInput 读取已被 Windows 识别的有线或蓝牙手柄；最小化后仍可继续映射。

## 下载与运行

[下载完整 Windows x64 程序包](GameControllerMapper-win-x64-background-fix.zip)，解压后运行 `GameControllerMapper.exe`。压缩包已包含完整构建输出及 `GameInputRedist.msi`；仅在系统提示缺少 GameInput 时安装该 MSI。

## 功能

- 按键映射为键盘组合键、鼠标按键或滚轮
- 左、右摇杆可映射为方向键或鼠标移动
- 支持配置文件、设备输入检测与全局紧急停用（`Ctrl + Alt + F12`）
- 关闭窗口会释放模拟输入并彻底退出进程

## 使用

1. 在 Windows 中连接或配对手柄。
2. 运行 `GameControllerMapper.exe`，选择手柄和配置文件。
3. 设置映射后打开右上角开关；最小化窗口不影响映射。

若系统缺少 Microsoft GameInput 运行时，请安装发布目录中的 `GameInputRedist.msi`。

> Windows 不允许普通权限程序向管理员权限的游戏注入输入；遇到此情况，请以管理员身份运行本程序。

## 开发

```powershell
dotnet build -c Release
dotnet publish -c Release -o publish
```

常用校验：

```powershell
Start-Process .\bin\Release\net8.0-windows10.0.19041.0\win-x64\GameControllerMapper.exe -ArgumentList --self-test -Wait
Start-Process .\bin\Release\net8.0-windows10.0.19041.0\win-x64\GameControllerMapper.exe -ArgumentList --gameinput-thread-test -Wait
```
