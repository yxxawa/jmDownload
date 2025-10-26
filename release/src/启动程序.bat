@echo off
chcp 65001 >nul
title JMComic 下载器

echo.
echo ========================================
echo    JMComic 漫画下载器 GUI 启动程序
echo ========================================
echo.

REM 检查Python是否安装
python --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] 未检测到Python，请先安装Python 3.7+
    echo 下载地址: https://www.python.org/downloads/
    pause
    exit /b 1
)

echo [信息] Python环境检测通过
echo.

REM 检查jmcomic是否安装
echo [信息] 正在检查jmcomic模块...
python -c "import jmcomic" >nul 2>&1
if %errorlevel% neq 0 (
    echo [警告] 未检测到jmcomic模块
    echo.
    set /p install_choice="是否尝试自动安装? (y/n，直接回车跳过): "
    
    if /i "!install_choice!"=="y" (
        echo.
        echo [信息] 正在安装jmcomic...
        pip install jmcomic -U
        if %errorlevel% neq 0 (
            echo.
            echo [错误] jmcomic安装失败
            echo [提示] 如果已手动安装，请忽略此错误，按任意键继续
            pause
        ) else (
            echo.
            echo [信息] jmcomic安装成功
        )
    ) else (
        echo.
        echo [信息] 跳过自动安装，尝试直接启动...
        echo [提示] 如果启动失败，请手动执行: pip install jmcomic -U
    )
) else (
    echo [信息] jmcomic模块已安装
)

echo [信息] 正在启动GUI界面...
echo.

REM 启动GUI程序
python jmcomic_gui.py

if %errorlevel% neq 0 (
    echo.
    echo [错误] 程序运行出错
    echo.
    echo 可能的原因:
    echo 1. jmcomic模块未正确安装
    echo 2. Python环境配置问题
    echo.
    echo 请尝试手动执行: pip install jmcomic -U
    echo 或直接运行: python jmcomic_gui.py
    echo.
    pause
)
