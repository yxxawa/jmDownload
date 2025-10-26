#!/usr/bin/env python3
"""
快速启动脚本
检查环境并启动 JMComic GUI
"""

import sys
import subprocess
import importlib.util

def check_python_version():
    """检查Python版本"""
    if sys.version_info < (3, 7):
        print("❌ 错误: 需要 Python 3.7 或更高版本")
        print(f"当前版本: {sys.version}")
        return False
    print(f"✅ Python 版本检查通过: {sys.version.split()[0]}")
    return True

def check_module(module_name):
    """检查模块是否已安装"""
    try:
        spec = importlib.util.find_spec(module_name)
        return spec is not None
    except (ImportError, ValueError, AttributeError):
        return False

def install_jmcomic():
    """安装jmcomic模块"""
    print("\n⚠️  检测到 jmcomic 模块可能未正确安装")
    user_input = input("是否尝试自动安装？(y/n，直接回车跳过): ").strip().lower()
    
    if user_input == 'y':
        print("\n正在安装 jmcomic...")
        try:
            subprocess.check_call([sys.executable, "-m", "pip", "install", "jmcomic", "-U"])
            print("✅ jmcomic 安装成功")
            return True
        except (subprocess.CalledProcessError, KeyboardInterrupt) as e:
            print("\n❌ jmcomic 自动安装失败")
            print("请手动执行: pip install jmcomic -U")
            return False
    else:
        print("\n⏭️  跳过自动安装，尝试直接启动...")
        print("如果启动失败，请手动执行: pip install jmcomic -U")
        return True  # 返回True继续尝试启动

def main():
    """主函数"""
    print("=" * 50)
    print("  JMComic 漫画下载器 GUI 启动程序")
    print("=" * 50)
    print()
    
    # 检查Python版本
    if not check_python_version():
        sys.exit(1)
    
    # 检查jmcomic模块
    print("\n正在检查 jmcomic 模块...")
    if not check_module("jmcomic"):
        if not install_jmcomic():
            print("\n提示: 如果已手动安装 jmcomic，请忽略上述错误，程序将尝试继续运行")
            print("按回车键继续...")
            input()
    else:
        print("✅ jmcomic 模块已安装")
    
    # 检查tkinter
    if not check_module("tkinter"):
        print("❌ 错误: 未检测到 tkinter 模块")
        print("请安装完整版Python或安装tkinter包")
        sys.exit(1)
    
    print("\n🚀 正在启动 GUI 界面...\n")
    
    # 导入并运行GUI
    try:
        import jmcomic_gui
        jmcomic_gui.main()
    except ImportError as e:
        print(f"\n❌ 导入错误: {str(e)}")
        print("\n可能的原因:")
        print("1. jmcomic 模块未正确安装")
        print("2. Python 环境配置问题")
        print("\n请尝试手动执行以下命令安装:")
        print("   pip install jmcomic -U")
        print("\n或者直接运行: python jmcomic_gui.py")
        input("\n按回车键退出...")
        sys.exit(1)
    except Exception as e:
        print(f"\n❌ 程序运行出错: {str(e)}")
        import traceback
        traceback.print_exc()
        input("\n按回车键退出...")
        sys.exit(1)

if __name__ == "__main__":
    main()
