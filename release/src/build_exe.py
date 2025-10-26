"""
JMComic 下载器 - EXE 打包脚本
使用 PyInstaller 将程序打包成独立的可执行文件
"""
import os
import subprocess
import sys

def build_exe():
    """打包程序为 EXE"""
    print("=" * 60)
    print("JMComic 下载器 - EXE 打包工具")
    print("=" * 60)
    print()
    
    # 确认当前目录
    current_dir = os.path.dirname(os.path.abspath(__file__))
    os.chdir(current_dir)
    print(f"📁 工作目录: {current_dir}")
    print()
    
    # 检查主程序文件
    if not os.path.exists("jmcomic_gui.py"):
        print("❌ 错误: 找不到 jmcomic_gui.py 文件！")
        return False
    
    # PyInstaller 打包命令
    # --onefile: 打包成单个 exe 文件
    # --windowed: 不显示控制台窗口（GUI 程序）
    # --name: 指定输出的 exe 文件名
    # --clean: 清理临时文件
    cmd = [
        "pyinstaller",
        "--onefile",              # 单文件模式
        "--windowed",             # 无控制台窗口
        "--name=JMComic下载器",   # exe 文件名
        "--clean",                # 清理缓存
        "--noconfirm",            # 不询问覆盖
        "jmcomic_gui.py"          # 主程序文件
    ]
    
    # 如果有图标文件，添加图标参数
    if os.path.exists("icon.ico"):
        cmd.insert(-1, "--icon=icon.ico")
        print("🎨 检测到图标文件，将使用自定义图标")
    else:
        print("ℹ️  未检测到 icon.ico，将使用默认图标")
    
    print()
    print("🔨 开始打包...")
    print(f"📋 执行命令: {' '.join(cmd)}")
    print()
    print("-" * 60)
    
    try:
        # 执行打包命令
        result = subprocess.run(cmd, check=True)
        
        print("-" * 60)
        print()
        print("✅ 打包成功！")
        print()
        print("📦 输出文件位置:")
        exe_path = os.path.join(current_dir, "dist", "JMComic下载器.exe")
        print(f"   {exe_path}")
        print()
        print("📝 说明:")
        print("   1. exe 文件在 dist 文件夹中")
        print("   2. 可以直接复制到任何 Windows 电脑运行")
        print("   3. 无需 Python 环境和任何依赖库")
        print("   4. 首次运行可能需要几秒钟解压")
        print()
        print("🧹 清理提示:")
        print("   - build 文件夹: 可以删除（临时文件）")
        print("   - JMComic下载器.spec: 可以删除（打包配置）")
        print("   - dist 文件夹: 保留（包含最终的 exe 文件）")
        print()
        
        return True
        
    except subprocess.CalledProcessError as e:
        print("-" * 60)
        print()
        print(f"❌ 打包失败！错误代码: {e.returncode}")
        print()
        print("💡 可能的解决方案:")
        print("   1. 确保已安装 PyInstaller: pip install pyinstaller")
        print("   2. 关闭所有正在运行的程序实例")
        print("   3. 以管理员身份运行此脚本")
        print()
        return False
    
    except Exception as e:
        print(f"❌ 发生错误: {str(e)}")
        return False

if __name__ == "__main__":
    success = build_exe()
    print()
    print("=" * 60)
    
    if success:
        input("按回车键退出...")
    else:
        input("按回车键退出...")
        sys.exit(1)
