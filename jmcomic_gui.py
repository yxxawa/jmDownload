"""
JMComic 下载器 GUI 界面
基于 Tkinter 的图形化界面，方便使用 JMComic 爬虫
"""

import tkinter as tk
from tkinter import messagebox, filedialog
import ttkbootstrap as ttk
from ttkbootstrap.constants import *
from ttkbootstrap.widgets.scrolled import ScrolledText
import threading
import os
import sys
import jmcomic
from jmcomic import JmOption
from pathlib import Path
from PIL import Image, ImageTk
import requests
from io import BytesIO
import json
import datetime


class JMComicGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("JMComic 下载器 v1.1")
        self.root.geometry("1326x933")  # 设置默认窗口大小
        self.root.resizable(True, True)
        
        # 下载状态标志
        self.is_downloading = False
        self.stop_requested = False  # 停止下载标志
        self.option = None
        self.current_album_info = None  # 存储当前本子信息
        self.current_cover_album_id = None  # 存储当前封面对应的本子ID
        
        # 滚动幅度，统一滚动幅度，保证流畅性
        self.scroll_units = 1
        
        # 排行榜缓存
        self.ranking_cache = {
            'day': None,
            'week': None,
            'month': None
        }
        self.ranking_cache_time = {
            'day': 0,
            'week': 0,
            'month': 0
        }
        
        # 缓存文件路径
        if getattr(sys, 'frozen', False):
            # 如果是打包后的exe文件，使用exe文件所在目录
            application_path = os.path.dirname(sys.executable)
        else:
            # 如果是Python脚本，使用当前工作目录
            application_path = os.getcwd()
        
        # 创建界面
        self.create_widgets()
        
        # 加载默认配置
        self.load_default_config()
    
    

    
    def _get_end_of_day_timestamp(self):
        """获取当天24点的时间戳"""
        import time
        import datetime
        # 获取当前日期
        today = datetime.date.today()
        # 获取明天的日期
        tomorrow = today + datetime.timedelta(days=1)
        # 获取明天0点的时间戳
        tomorrow_timestamp = time.mktime(tomorrow.timetuple())
        return tomorrow_timestamp
    
    def log(self, content, level="INFO"):
        """添加时间戳的日志方法"""
        import datetime
        # 生成当前时间戳（格式：年-月-日 时:分:秒）
        time_stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        # 拼接日志内容
        log_content = f"[{time_stamp}][{level}] {content}"
        
        # 输出到日志文本框
        self.log_text.text.insert(tk.END, log_content + "\n", level)
        self.log_text.text.see(tk.END)
        self.root.update_idletasks()
    
    def fade_in_window(self, window, duration=200):
        """窗口淡入动画"""
        window.attributes('-alpha', 0.0)
        steps = 20
        step_time = duration // steps
        
        def animate(step=0):
            if step <= steps:
                alpha = step / steps
                window.attributes('-alpha', alpha)
                window.after(step_time, lambda: animate(step + 1))
        
        animate()
    
    def scale_in_window(self, window, duration=150):
        """窗口缩放动画"""
        # 获取目标大小和位置
        window.update_idletasks()
        width = window.winfo_reqwidth()
        height = window.winfo_reqheight()
        x = window.winfo_x()
        y = window.winfo_y()
        
        steps = 15
        step_time = duration // steps
        
        def animate(step=0):
            if step <= steps:
                scale = 0.7 + (0.3 * step / steps)
                current_width = int(width * scale)
                current_height = int(height * scale)
                offset_x = int((width - current_width) / 2)
                offset_y = int((height - current_height) / 2)
                
                window.geometry(f"{current_width}x{current_height}+{x+offset_x}+{y+offset_y}")
                window.after(step_time, lambda: animate(step + 1))
            else:
                window.geometry(f"{width}x{height}+{x}+{y}")
        
        animate()
    
    def button_press_animation(self, button):
        """按钮点击动画"""
        original_style = button.cget('style') if hasattr(button, 'cget') else None
        
        # 按下效果
        try:
            button.state(['pressed'])
        except Exception as e:
            # 按钮状态设置失败，不影响程序运行
            pass
        
        # 100ms后恢复
        def restore():
            try:
                button.state(['!pressed'])
            except Exception as e:
                # 按钮状态恢复失败，不影响程序运行
                pass
        
        self.root.after(100, restore)
    
    def create_widgets(self):
        """创建所有GUI组件"""
        # 创建主框架 - 使用左右分栏布局
        main_container = ttk.Frame(self.root, padding="10")
        main_container.grid(row=0, column=0, sticky=tk.W+tk.E+tk.N+tk.S)
        
        # 绑定窗口关闭事件
        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)
        
        # 配置网格权重
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_container.columnconfigure(0, weight=1)  # 左侧主区域
        main_container.columnconfigure(1, weight=0)  # 右侧封面区域，固定宽度
        main_container.rowconfigure(0, weight=1)
        
        # 左侧主区域
        left_frame = ttk.Frame(main_container)
        left_frame.grid(row=0, column=0, sticky=tk.W+tk.E+tk.N+tk.S, padx=(0, 10))
        left_frame.columnconfigure(0, weight=1)
        left_frame.rowconfigure(5, weight=1)
        
        # 右侧封面区域（固定宽度）
        right_frame = ttk.Frame(main_container)
        right_frame.grid(row=0, column=1, sticky=tk.N, padx=0)
        
        # 封面预览框
        cover_frame = ttk.LabelFrame(right_frame, text="封面预览")
        cover_frame.pack(fill=tk.BOTH, expand=False)
        
        # 封面图片标签（固定大小）
        self.cover_label = ttk.Label(cover_frame, text="暂无封面\n\n请输入本子ID\n并点击'显示封面'", 
                                    foreground='gray', anchor='center', justify='center',
                                    width=25, cursor='hand2')  # 设置固定宽度，鼠标变手型
        self.cover_label.pack(pady=5)
        self.current_cover_image = None  # 保存当前封面图片引用，防止被垃圾回收
        self.full_cover_image = None  # 保存完整尺寸的封面图片，用于放大显示
        
        # 绑定点击事件，点击封面可以放大查看
        self.cover_label.bind('<Button-1>', self.show_full_cover)
        
        # 作者信息框
        info_frame = ttk.LabelFrame(right_frame, text="关于")
        info_frame.pack(fill=tk.BOTH, expand=False, pady=(10, 0))
        
        # 作者信息
        info_text = ttk.Label(info_frame, text="作者: yxxawa\n\n版本: 1.1\n\n蕉♂流群: 21013274471\n\nAPI项目:",
                             foreground='gray', font=('Arial', 9), justify='left')
        info_text.pack(anchor='w')
        
        # API项目链接（可点击）
        api_link = ttk.Label(info_frame, text="github.com/hect0x7/\nJMComic-Crawler-Python", 
                            foreground='blue', font=('Arial', 8), justify='left', cursor='hand2')
        api_link.pack(anchor='w', padx=(0, 0))
        api_link.bind('<Button-1>', lambda e: self.open_url('https://github.com/hect0x7/JMComic-Crawler-Python'))
        
        # ===== 搜索栏 =====
        search_frame = ttk.Frame(left_frame)
        search_frame.grid(row=0, column=0, sticky=tk.W+tk.E, pady=(0, 5))
        search_frame.columnconfigure(1, weight=1)
        
        ttk.Label(search_frame, text="搜索:", font=('Arial', 10)).grid(row=0, column=0, sticky=tk.W, padx=(0, 5))
        self.search_entry = ttk.Entry(search_frame)
        self.search_entry.grid(row=0, column=1, sticky=tk.W+tk.E, padx=5)
        
        # 添加占位符文字功能
        self.search_placeholder = "输入关键词搜索本子..."
        self.search_entry.insert(0, self.search_placeholder)
        self.search_entry.config(foreground='gray')
        
        def on_search_focus_in(event):
            if self.search_entry.get() == self.search_placeholder:
                self.search_entry.delete(0, tk.END)
                self.search_entry.config(foreground='black')
        
        def on_search_focus_out(event):
            if not self.search_entry.get():
                self.search_entry.insert(0, self.search_placeholder)
                self.search_entry.config(foreground='gray')
        
        # 搜索时如果是占位符，清空它
        def on_search_return(event):
            if self.search_entry.get() == self.search_placeholder:
                self.search_entry.delete(0, tk.END)
                self.search_entry.config(foreground='black')
            self.show_search_dialog()
        
        self.search_entry.bind('<FocusIn>', on_search_focus_in)
        self.search_entry.bind('<FocusOut>', on_search_focus_out)
        self.search_entry.bind('<Return>', on_search_return)
        
        ttk.Button(search_frame, text="搜索", command=self.on_search_button_click, width=10).grid(row=0, column=2, padx=5)
        
        # ===== 标题 =====
        title_label = ttk.Label(left_frame, text="JMComic 漫画下载器", 
                                font=('Arial', 16, 'bold'))
        title_label.grid(row=1, column=0, pady=10, sticky=tk.W)
        
        # ===== 下载输入区域 =====
        input_frame = ttk.LabelFrame(left_frame, text="下载设置")
        input_frame.grid(row=2, column=0, sticky=tk.W+tk.E, pady=5)
        input_frame.columnconfigure(1, weight=1)
        
        # 本子ID输入
        ttk.Label(input_frame, text="本子ID:").grid(row=0, column=0, sticky=tk.W, pady=5)
        
        # 创建一个水平布局的框架来容纳输入框和显示标签
        id_frame = ttk.Frame(input_frame)
        id_frame.grid(row=0, column=1, sticky=tk.W+tk.E, padx=5, pady=5)
        id_frame.columnconfigure(0, weight=0)
        id_frame.columnconfigure(1, weight=1)
        
        self.album_id_entry = ttk.Entry(id_frame, width=15)
        self.album_id_entry.grid(row=0, column=0, sticky=tk.W)
        # 默认为空，不填充任何ID
        
        # 绑定输入事件，输入时自动获取本子名字
        self.album_id_entry.bind('<KeyRelease>', self.on_id_input_change)
        
        # 显示本子名字的标签（浅色、只读）
        self.album_title_label = ttk.Label(id_frame, text="", foreground='gray', font=('Arial', 9))
        self.album_title_label.grid(row=0, column=1, sticky=tk.W, padx=(5, 0))
        
        # 添加显示封面按钮
        ttk.Button(id_frame, text="显示封面", command=self.on_show_cover_click, 
                  width=10).grid(row=0, column=2, padx=(10, 0))
        
        ttk.Label(input_frame, text="提示: 可输入多个ID，用空格或逗号分隔", 
                 font=('Arial', 8), foreground='gray').grid(row=1, column=1, sticky=tk.W, padx=5)
        
        # 下载路径
        ttk.Label(input_frame, text="保存路径:").grid(row=2, column=0, sticky=tk.W, pady=5)
        path_frame = ttk.Frame(input_frame)
        path_frame.grid(row=2, column=1, sticky=tk.W+tk.E, pady=5)
        path_frame.columnconfigure(0, weight=1)
        
        self.download_path_entry = ttk.Entry(path_frame)
        self.download_path_entry.grid(row=0, column=0, sticky=tk.W+tk.E, padx=(0, 5))
        # 默认路径为当前工作目录下的JMDownLoad文件夹
        default_path = Path(os.getcwd()) / "JMDownLoad"
        self.download_path_entry.insert(0, str(default_path))
        
        ttk.Button(path_frame, text="浏览...", command=self.browse_folder, 
                  width=10).grid(row=0, column=1)
        
        # ===== 高级选项区域 =====
        options_frame = ttk.LabelFrame(left_frame, text="高级选项")
        options_frame.grid(row=3, column=0, sticky=tk.W+tk.E, pady=5)
        options_frame.columnconfigure(1, weight=1)
        
        # 图片格式
        ttk.Label(options_frame, text="图片格式:").grid(row=0, column=0, sticky=tk.W, pady=5)
        self.image_format_var = tk.StringVar(value=".png")  # 默认使用PNG格式
        format_combo = ttk.Combobox(options_frame, textvariable=self.image_format_var, 
                                    values=["原始格式", ".png", ".jpg", ".webp"], 
                                    state="readonly", width=15)
        format_combo.grid(row=0, column=1, sticky=tk.W, padx=5, pady=5)
        
        # 下载线程数
        ttk.Label(options_frame, text="并发章节数:").grid(row=0, column=2, sticky=tk.W, pady=5, padx=(20, 0))
        self.thread_count_var = tk.IntVar(value=1)
        thread_spin = ttk.Spinbox(options_frame, from_=1, to=5, 
                                 textvariable=self.thread_count_var, width=10)
        thread_spin.grid(row=0, column=3, sticky=tk.W, padx=5, pady=5)
        
        # 图片并发数
        ttk.Label(options_frame, text="并发图片数:").grid(row=1, column=0, sticky=tk.W, pady=5)
        self.image_thread_var = tk.IntVar(value=5)
        image_thread_spin = ttk.Spinbox(options_frame, from_=1, to=20, 
                                       textvariable=self.image_thread_var, width=10)
        image_thread_spin.grid(row=1, column=1, sticky=tk.W, padx=5, pady=5)
        
        
        # ===== 控制按钮 =====
        button_frame = ttk.Frame(left_frame)
        button_frame.grid(row=4, column=0, pady=10)
        
        self.download_btn = ttk.Button(button_frame, text="开始下载", 
                                       command=self.on_start_download_click, width=15)
        self.download_btn.grid(row=0, column=0, padx=5)
        
        self.stop_btn = ttk.Button(button_frame, text="停止下载", 
                                   command=self.stop_download, width=15, state=tk.DISABLED)
        self.stop_btn.grid(row=0, column=1, padx=5)
        
        ttk.Button(button_frame, text="打开下载目录", 
                  command=self.open_download_folder, width=15).grid(row=0, column=2, padx=5)
        
        ttk.Button(button_frame, text="清空日志", 
                  command=self.clear_log, width=15).grid(row=0, column=3, padx=5)
        
        # ===== 风格选择区域 =====
        # 移除主界面的风格选择区域，改为弹窗选择

        # ===== 日志输出区域 =====
        log_frame = ttk.LabelFrame(left_frame, text="下载日志")
        log_frame.grid(row=5, column=0, sticky=tk.W+tk.E+tk.N+tk.S, pady=5)  # 从row=6改为row=5
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        
        # 使用 ScrolledText 代替 scrolledtext.ScrolledText
        self.log_text = ScrolledText(log_frame, height=12, wrap=tk.WORD, autohide=True)
        self.log_text.pack(fill=tk.BOTH, expand=True)
        
        # 日志文本标签
        self.log_text.text.tag_config('INFO', foreground='black')
        self.log_text.text.tag_config('SUCCESS', foreground='green')
        self.log_text.text.tag_config('ERROR', foreground='red')
        self.log_text.text.tag_config('WARNING', foreground='orange')
        
        # ===== 底部信息栏 =====
        footer_frame = ttk.Frame(left_frame)
        footer_frame.grid(row=7, column=0, sticky=tk.W+tk.E, pady=5)
        footer_frame.columnconfigure(1, weight=1)
        
        # 移除左下角的深色模式按钮（根据用户要求）
        # 左侧：主题切换按钮
        # self.theme_btn = ttk.Button(footer_frame, text="🌙 深色模式", 
        #                             command=self.toggle_theme, width=12)
        # self.theme_btn.grid(row=0, column=0, padx=(0, 5))
        
        # 中间：使用提示（调整位置）
        ttk.Label(footer_frame, text="使用提示: 支持下载本子ID(如422866)或章节ID(如p456)，多个ID用空格或逗号分隔", 
                 font=('Arial', 8)).grid(row=0, column=0, sticky=tk.W, padx=10)  # 从column=1改为column=0
        
        # 右侧：排行榜按钮
        buttons_frame = ttk.Frame(footer_frame)
        buttons_frame.grid(row=0, column=1, padx=0)  # 从column=2改为column=1
        
        ranking_btn = ttk.Button(buttons_frame, text="查看排行榜", 
                               command=self.on_ranking_button_click, width=15)
        ranking_btn.pack(side=tk.LEFT, padx=(0, 5))
    
    def on_id_input_change(self, event):
        """输入框内容变化时的处理"""
        # 立即更新下载路径
        self.update_download_path()
        
        # 延迟800ms后获取名字，避免频繁请求
        if hasattr(self, '_fetch_timer'):
            self.root.after_cancel(self._fetch_timer)
        self._fetch_timer = self.root.after(800, self.fetch_album_title_auto)
    
    def update_download_path(self):
        """根据输入的ID自动更新下载路径"""
        album_id = self.album_id_entry.get().strip()
        
        # 直接使用当前工作目录作为基础路径
        base_dir = Path(os.getcwd())
        
        if not album_id:
            # 如果没有输入ID，使用默认路径：当前工作目录\JMDownLoad
            default_path = base_dir / "JMDownLoad"
            self.download_path_entry.delete(0, tk.END)
            self.download_path_entry.insert(0, str(default_path))
            return
        
        # 解析ID
        ids = self.parse_ids(album_id)
        if not ids:
            return
        
        first_id = ids[0]
        
        # 如果是章节ID，去掉p前缀
        if first_id.lower().startswith('p'):
            first_id = first_id[1:]
        
        # 设置路径为: 当前工作目录\JMDownLoad\本子ID
        new_path = base_dir / "JMDownLoad" / first_id
        self.download_path_entry.delete(0, tk.END)
        self.download_path_entry.insert(0, str(new_path))
    
    def fetch_album_title_auto(self):
        """自动获取本子标题（输入时触发）"""
        album_id = self.album_id_entry.get().strip()
        
        # 清空之前的显示
        if not album_id:
            self.album_title_label.config(text="")
            self.current_album_info = None
            return
        
        # 只处理单个ID
        if ' ' in album_id or ',' in album_id:
            self.album_title_label.config(text="--- [仅显示单个ID的名称]", foreground='gray')
            self.current_album_info = None
            return
        
        # 如果是章节ID，不获取名字
        if album_id.lower().startswith('p'):
            self.album_title_label.config(text="--- [章节ID]", foreground='gray')
            self.current_album_info = None
            return
        
        # 在新线程中获取名字，避免阻塞界面
        def fetch_thread():
            try:
                # 使用jmcomic API获取本子信息

                
                # 创建临时配置
                temp_option = JmOption.default()
                
                # 使用正确的API获取本子详情
                try:
                    # 通过JmOption创建客户端
                    client = temp_option.build_jm_client()  # type: ignore
                    album = client.get_album_detail(album_id)  # type: ignore
                except (AttributeError, ImportError):
                    # 如果API不可用，返回None
                    album = None
                
                if album and hasattr(album, 'title'):
                    title = album.title
                    # 保存本子信息
                    self.current_album_info = album
                    # 更新UI必须在主线程
                    self.root.after(0, lambda: self.album_title_label.config(
                        text=f" --- [{title}]", foreground='darkgray'
                    ))
                else:
                    self.current_album_info = None
                    self.root.after(0, lambda: self.album_title_label.config(
                        text="", foreground='gray'
                    ))
                    
            except Exception as e:
                # 出错时不显示错误，保持静默
                self.current_album_info = None
                self.root.after(0, lambda: self.album_title_label.config(text="", foreground='gray'))
        
        # 启动线程
        thread = threading.Thread(target=fetch_thread, daemon=True)
        thread.start()
    
    def show_cover(self, album_id=None):
        """显示封面"""
        # 如果没有传入album_id，从输入框获取
        if album_id is None:
            album_id = self.album_id_entry.get().strip()
        
        if not album_id:
            messagebox.showwarning("警告", "请先输入本子ID！")
            return
        
        # 只处理单个ID
        ids = self.parse_ids(album_id)
        if not ids:
            messagebox.showwarning("警告", "请输入有效的本子ID！")
            return
        
        first_id = ids[0]
        
        # 如果是章节ID
        if first_id.lower().startswith('p'):
            messagebox.showwarning("警告", "章节ID没有封面，请输入本子ID！")
            return
        
        # 检查是否已经显示了这个封面
        if self.current_cover_album_id == first_id:
            self.log(f"封面已经是 ID {first_id} 的封面，无需重复获取", "INFO")
            return
        
        # 在新线程中获取封面
        def fetch_cover_thread():
            self.log(f"开始获取封面: {first_id}", "INFO")
            self.root.after(0, lambda: self.cover_label.config(text="正在加载封面...", foreground='blue'))
            
            # 使用jmcomic API下载封面

            import tempfile
            
            temp_option = JmOption.default()
            self.log("正在创建客户端...", "INFO")
            
            try:
                client = temp_option.new_jm_client()  # type: ignore
                self.log("客户端创建成功", "INFO")
                
                # 创建临时目录
                temp_dir = tempfile.mkdtemp()
                
                try:
                    # 设置临时保存路径
                    temp_file = os.path.join(temp_dir, "cover.jpg")
                    
                    # 使用 jmcomic 库的 download_album_cover 方法（内置代理支持）
                    self.log("正在下载封面图片...", "INFO")
                    client.download_album_cover(first_id, temp_file)  # type: ignore
                    
                    self.log("正在处理图片...", "INFO")
                    # 读取文件到内存
                    with open(temp_file, 'rb') as f:
                        image_data = f.read()
                    
                    # 转换为PIL Image
                    image = Image.open(BytesIO(image_data))
                    
                    # 保存完整尺寸的图片，用于放大显示
                    self.full_cover_image = image.copy()
                    
                    # 固定封面大小：宽度200px，高度按比例计算，最大280px
                    target_width = 200
                    ratio = target_width / image.width
                    new_height = int(image.height * ratio)
                    # 限制最大高度
                    if new_height > 280:
                        new_height = 280
                        ratio = new_height / image.height
                        target_width = int(image.width * ratio)
                    
                    image = image.resize((target_width, new_height), Image.Resampling.LANCZOS)
                    
                    # 转换为Tkinter可用的PhotoImage
                    photo_img = ImageTk.PhotoImage(image)
                    
                    # 更新UI
                    self.root.after(0, lambda: self.update_cover_display(photo_img, first_id))
                    self.log("✅ 封面加载成功！点击封面可放大查看", "SUCCESS")
                    
                finally:
                    # 清理临时文件
                    import shutil
                    if os.path.exists(temp_dir):
                        shutil.rmtree(temp_dir)
                
            except Exception as e:
                self.log(f"下载封面失败: {e}", "ERROR")
                import traceback
                error_detail = traceback.format_exc()
                # 输出详细错误信息
                self.log("-" * 60, "ERROR")
                for line in error_detail.split('\n'):
                    if line.strip():
                        self.log(line, "ERROR")
                self.log("-" * 60, "ERROR")
                self.root.after(0, lambda: self.cover_label.config(
                    text="封面下载失败", foreground='red'
                ))
        
        thread = threading.Thread(target=fetch_cover_thread, daemon=True)
        thread.start()
    
    def update_cover_display(self, photo, album_id=None):
        """更新封面显示"""
        self.current_cover_image = photo  # 保存引用
        self.cover_label.config(image=photo, text="", compound='image')  # 确保显示图片
        # 记录当前显示的封面ID
        if album_id:
            self.current_cover_album_id = album_id
    
    def show_full_cover(self, event=None):
        """显示放大的封面图片"""
        # 检查是否有完整尺寸的封面图片
        if self.full_cover_image is None:
            return
        
        # 创建一个新的顶层窗口
        cover_window = tk.Toplevel(self.root)
        cover_window.title("📷 封面查看")
        
        # 获取屏幕尺寸
        screen_width = cover_window.winfo_screenwidth()
        screen_height = cover_window.winfo_screenheight()
        
        # 获取原始图片尺寸
        img_width = self.full_cover_image.width
        img_height = self.full_cover_image.height
        
        # 计算缩放比例，确保图片不超过屏幕的80%
        max_width = int(screen_width * 0.8)
        max_height = int(screen_height * 0.8)
        
        scale = min(max_width / img_width, max_height / img_height, 1.0)  # 不放大，只缩小
        new_width = int(img_width * scale)
        new_height = int(img_height * scale)
        
        # 缩放图片
        display_image = self.full_cover_image.resize((new_width, new_height), Image.Resampling.LANCZOS)
        photo = ImageTk.PhotoImage(display_image)
        
        # 设置窗口大小
        cover_window.geometry(f"{new_width + 40}x{new_height + 80}")
        
        # 居中显示窗口
        x = (screen_width - new_width - 40) // 2
        y = (screen_height - new_height - 80) // 2
        cover_window.geometry(f"+{x}+{y}")
        
        # 显示图片
        image_label = tk.Label(cover_window, image=photo)
        image_label.image = photo  # 保存引用
        image_label.pack(padx=20, pady=20)
        
        # 添加提示文本
        tip_label = tk.Label(cover_window, text=f"📊 原始尺寸: {img_width}x{img_height}  |  显示尺寸: {new_width}x{new_height}", 
                            font=('Arial', 9), fg='gray')
        tip_label.pack(pady=(0, 10))
        
        # 点击图片关闭窗口
        image_label.bind('<Button-1>', lambda e: cover_window.destroy())
        
        # ESC键关闭窗口
        cover_window.bind('<Escape>', lambda e: cover_window.destroy())
        
        # 设置窗口为模态窗口
        cover_window.transient(self.root)
        cover_window.grab_set()
    
    def fetch_album_title(self):
        """获取本子标题"""
        album_id = self.album_id_entry.get().strip()
        
        # 清空之前的显示
        self.album_title_label.config(text="")
        
        if not album_id:
            return
        
        # 如果是多个ID，只获取第一个
        ids = self.parse_ids(album_id)
        if not ids:
            return
        
        first_id = ids[0]
        
        # 如果是章节ID，不获取名字
        if first_id.lower().startswith('p'):
            self.album_title_label.config(text="---- [章节ID]")
            return
        
        # 在新线程中获取名字，避免阻塞界面
        def fetch_thread():
            try:
                self.album_title_label.config(text="---- [正在获取...]")
                self.root.update_idletasks()
                
                # 使用jmcomic API获取本子信息

                
                # 创建临时配置
                temp_option = JmOption.default()
                
                # 获取本子详情
                # 通过 JmOption 创建客户端
                try:
                    client = temp_option.new_jm_client()  # type: ignore
                    album = client.get_album_detail(first_id)  # type: ignore
                except AttributeError:
                    # 如果API不可用，返回None
                    album = None
                
                if album and hasattr(album, 'title'):
                    title = album.title
                    # 更新UI必须在主线程
                    self.root.after(0, lambda: self.album_title_label.config(
                        text=f"---- [{title}]", foreground='green'
                    ))
                else:
                    self.root.after(0, lambda: self.album_title_label.config(
                        text="---- [获取失败]", foreground='red'
                    ))
                    
            except Exception as e:
                error_msg = str(e)
                self.root.after(0, lambda: self.album_title_label.config(
                    text=f"---- [错误: {error_msg[:20]}...]", foreground='red'
                ))
        
        # 启动线程
        thread = threading.Thread(target=fetch_thread, daemon=True)
        thread.start()
    
    def load_default_config(self):
        """加载默认配置"""
        self.log("系统初始化完成，准备就绪！", "SUCCESS")
    

    
    def on_search_button_click(self):
        """搜索按钮点击事件"""
        # 按钮点击动画
        search_btn = None
        # 找到搜索按钮
        for widget in self.root.winfo_children():
            if isinstance(widget, ttk.Frame):
                for child in widget.winfo_children():
                    if isinstance(child, ttk.Frame):
                        for subchild in child.winfo_children():
                            if isinstance(subchild, ttk.Button) and subchild.cget('text') == '搜索':
                                search_btn = subchild
                                break
        
        if search_btn:
            self.button_press_animation(search_btn)
        
        # 处理占位符情况
        if self.search_entry.get() == self.search_placeholder:
            self.search_entry.delete(0, tk.END)
            self.search_entry.config(foreground='black')
        
        # 调用搜索方法
        self.root.after(100, self.show_search_dialog)
    
    def on_ranking_button_click(self):
        """排行榜按钮点击事件"""
        # 按钮点击动画
        ranking_btn = None
        # 找到排行榜按钮
        for widget in self.root.winfo_children():
            if isinstance(widget, ttk.Frame):
                for child in widget.winfo_children():
                    if isinstance(child, ttk.Frame):
                        for subchild in child.winfo_children():
                            if isinstance(subchild, ttk.Frame):
                                for btn in subchild.winfo_children():
                                    if isinstance(btn, ttk.Button) and '排行榜' in btn.cget('text'):
                                        ranking_btn = btn
                                        break
        
        if ranking_btn:
            self.button_press_animation(ranking_btn)
        
        # 调用排行榜方法
        self.root.after(100, self.show_ranking_dialog)
    
    def browse_folder(self):
        """浏览文件夹"""
        folder = filedialog.askdirectory()
        if folder:
            self.download_path_entry.delete(0, tk.END)
            self.download_path_entry.insert(0, folder)
    
    def open_download_folder(self):
        """打开当前设置的下载文件夹（处理路径无效、不存在的情况）"""
        import os
        import webbrowser  # 用于跨平台打开文件夹（Windows用os.startfile，Mac/Linux用webbrowser）

        # 1. 获取当前下载路径（和create_option保持一致，从输入框取）
        current_path = self.download_path_entry.get().strip()
        if not current_path:
            messagebox.showerror("错误", "下载路径为空！请先设置保存路径")
            return

        # 2. 转为绝对路径（避免相对路径问题）
        current_path = os.path.abspath(current_path)

        # 3. 检查路径是否存在
        if not os.path.exists(current_path):
            create_confirm = messagebox.askyesno(
                "路径不存在", 
                f"下载路径「{current_path}」不存在，是否先创建再打开？"
            )
            if create_confirm:
                try:
                    os.makedirs(current_path, exist_ok=True)
                    self.log(f"✅ 已创建路径：{current_path}", "SUCCESS")
                except Exception as e:
                    messagebox.showerror("打开失败", f"创建路径失败：{str(e)}（权限不足）")
                    return
            else:
                return

        # 4. 跨平台打开文件夹（避免Windows/Mac/Linux兼容性问题）
        try:
            if os.name == 'nt':  # Windows系统
                os.startfile(current_path)  # Windows专用方法
            else:  # Mac/Linux系统
                webbrowser.open(current_path)  # 跨平台方法
            self.log(f"✅ 已打开下载文件夹：{current_path}", "INFO")
        except Exception as e:
            # 捕获打开失败的异常（比如路径有特殊字符、权限不足）
            messagebox.showerror("打开失败", f"无法打开文件夹：{str(e)}")
            self.log(f"❌ 打开文件夹失败：{str(e)}", "ERROR")
    
    def create_option(self):
        try:

            import os
            
            # 1. 读取并验证下载路径
            current_download_path = self.download_path_entry.get().strip()
            if not current_download_path:
                messagebox.showerror("错误", "下载路径不能为空！")
                return None
            
            current_download_path = os.path.abspath(current_download_path)
            if not os.path.exists(current_download_path):
                os.makedirs(current_download_path, exist_ok=True)
            
            # 2. 获取用户选择的图片格式
            selected_format = self.image_format_var.get()
            # 处理用户选择的格式
            if selected_format == "原始格式":
                image_suffix = None  # 保持原始格式
            else:
                image_suffix = selected_format  # 使用用户选择的格式（如 .png, .jpg, .webp）
            
            # 3. 初始化 JmOption（使用完整配置，修复 postman 和 download 错误）
            option = JmOption(
                dir_rule={
                    "rule": "Bd_Pname",  # 使用 Bd_Pname 规则来创建有意义的文件夹名称
                    "base_dir": current_download_path
                },
                download={
                    "cache": True,
                    "image": {
                        "decode": True,
                        "suffix": image_suffix  # 使用用户选择的图片格式
                    },
                    "threading": {
                        "image": 30,
                        "photo": 24,
                        "max_workers": 3  # 并发数配置
                    }
                },
                client={
                    "cache": None,
                    "domain": [],
                    "postman": {
                        "type": "curl_cffi",
                        "meta_data": {
                            "impersonate": "chrome",
                            "headers": None,
                            "proxies": {}
                        }
                    },
                    "impl": "api",
                    "retry_times": 5
                },
                plugins={}
            )
            
            return option
        except Exception as e:
            messagebox.showerror("配置创建失败", f"出错原因：{str(e)}")
            return None
    
    def parse_ids(self, input_str):
        """解析ID：支持逗号/空格分隔"""
        if not input_str:
            return []
        # 先按逗号分割，再按空格分割，最后去空
        id_list = []
        for part in input_str.split(','):
            id_list.extend(part.split())
        return [id.strip() for id in id_list if id.strip()]
    
    def start_download(self):
        """开始下载（修正：不提前创建option，每次下载新ID时动态生成）"""
        if self.is_downloading:
            messagebox.showwarning("警告", "已有下载任务在进行中！")
            return
        
        input_ids = self.album_id_entry.get().strip()
        if not input_ids:
            messagebox.showwarning("警告", "请输入要下载的本子ID或章节ID！")
            return
        
        # 解析ID（去重+保留顺序）
        ids = self.parse_ids(input_ids)
        unique_ids = []
        for id in ids:
            if id not in unique_ids:
                unique_ids.append(id)
        if not unique_ids:
            messagebox.showwarning("警告", "没有有效的ID！")
            return
        
        # 显示下载确认，让用户知道要下载的ID数量
        confirm = messagebox.askyesno(
            "确认下载", 
            f"即将开始下载 {len(unique_ids)} 个ID，是否继续？\n\n{', '.join(unique_ids)}\n\n提示：点击「停止下载」按钮后，当前正在下载的内容会继续完成，之后才会停止。"
        )
        if not confirm:
            self.log("用户取消了下载", "INFO")
            return
        
        # 初始化下载状态（不创建option，留到download_single_id中动态创建）
        self.stop_requested = False
        self.is_downloading = True
        self.download_btn.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)
        
        # 在日志中显示要下载的ID列表
        self.log(f"开始下载任务，共 {len(unique_ids)} 个ID: {', '.join(unique_ids)}", "INFO")
        self.log("-" * 60, "INFO")

        # 启动第一个ID的下载（不再传option，让它自己动态生成）
        self.root.after(0, self.download_single_id)
    
    def download_task(self, ids, option):
        """下载任务（在后台线程执行）"""
        try:
            self.log(f"开始下载任务，共 {len(ids)} 个ID", "INFO")
            self.log(f"下载路径: {option.dir_rule.base_dir}", "INFO")
            self.log("-" * 60, "INFO")
            
            for idx, item_id in enumerate(ids, 1):
                # 检查是否请求停止
                if self.stop_requested:
                    self.log("下载已被用户取消", "WARNING")
                    break
                
                if not self.is_downloading:
                    self.log("下载已被用户取消", "WARNING")
                    break
                
                try:
                    self.log(f"[{idx}/{len(ids)}] 开始下载: {item_id}", "INFO")
                    
                    # 判断是本子还是章节
                    if item_id.lower().startswith('p'):
                        # 章节ID
                        photo_id = item_id[1:]  # 移除'p'前缀
                        jmcomic.download_photo(photo_id, option)
                        self.log(f"✅ 章节 {item_id} 下载完成", "SUCCESS")
                    else:
                        # 本子ID
                        jmcomic.download_album(item_id, option)
                        self.log(f"✅ 本子 {item_id} 下载完成", "SUCCESS")
                    
                except Exception as e:
                    # 检查是否是用户取消导致的错误
                    if self.stop_requested:
                        self.log(f"⚠️ 下载 {item_id} 被取消", "WARNING")
                        break
                    else:
                        self.log(f"❌ 下载 {item_id} 失败: {str(e)}", "ERROR")
            
            self.log("-" * 60, "INFO")
            if self.stop_requested:
                self.log("下载任务已取消！", "WARNING")
            else:
                self.log("所有下载任务完成！", "SUCCESS")
            
        except Exception as e:
            self.log(f"下载过程发生错误: {str(e)}", "ERROR")
        
        finally:
            # 恢复UI状态
            self.root.after(0, self.download_finished)
    
    def download_single_id(self):
        """逐个下载ID（修正：每次下载前重新创建option，用最新路径）"""
        # 1. 检查停止条件
        if self.stop_requested or not self.is_downloading:
            self.log("下载任务已停止（用户取消或无更多ID）", "WARNING")
            self.download_finished()
            return
        
        # 2. 获取当前输入框的ID（每次都重新获取，因为会自动删除）
        input_ids = self.album_id_entry.get().strip()
        if not input_ids:
            self.log("所有ID下载完成！", "SUCCESS")
            self.download_finished()
            return
        
        # 3. 解析当前第一个ID（要下载的ID）
        ids = self.parse_ids(input_ids)
        current_id = ids[0]
        total_remaining = len(ids)
        
        # 4. 关键：基于当前第一个ID，重新创建下载配置（确保路径正确）
        option = self.create_option()  # 每次下载都新生成option！新生成！
        if option is None:
            self.log("创建下载配置失败，停止当前ID下载", "ERROR")
            # 跳过当前ID，继续下一个（或停止，根据需求选）
            self.root.after(50, self.download_single_id)
            return
        
        # 5. 自动获取封面（仅本子ID）
        if not current_id.lower().startswith('p') and self.current_cover_album_id != current_id:
            self.log(f"自动获取封面: {current_id}", "INFO")
            self.show_cover(current_id)
        
        # 6. 绑定真实进度回调（不变）
        def progress_callback(current, total, info):
            # 检查是否已请求停止
            if self.stop_requested:
                self.log(f"⚠️ 检测到停止请求，当前下载完成后将停止", "WARNING")
            
            # 移除进度条和状态标签的更新（因为组件已被移除）
            # progress_percent = (current / total) * 100 if total > 0 else 0
            # self.root.after(0, lambda: self.progress_bar.config(value=progress_percent))
            # self.root.after(0, lambda: self.status_label.config(
            #     text=f"下载中（剩余{total_remaining}个）: {info}"
            # ))
        
        option.progress_callback = progress_callback  # 给新option绑定回调

        # 7. 后台下载当前ID（用新生成的option）
        def download_thread_func():
            try:
                self.log(f"开始下载（剩余{total_remaining}个）: {current_id}", "INFO")
                self.log(f"当前ID下载路径: {option.dir_rule.base_dir}", "INFO")  # 打印当前路径，验证是否正确
                
                # 下载逻辑（用新option，路径正确）
                if current_id.lower().startswith('p'):
                    photo_id = current_id[1:]
                    jmcomic.download_photo(photo_id, option)
                    self.log(f"✅ 章节 {current_id} 下载完成（路径：{option.dir_rule.base_dir}）", "SUCCESS")
                else:
                    jmcomic.download_album(current_id, option)
                    self.log(f"✅ 本子 {current_id} 下载完成（路径：{option.dir_rule.base_dir}）", "SUCCESS")
                
                # 8. 下载完成：删除输入框中已下载的ID，更新路径（不变）
                self.root.after(0, self.remove_downloaded_id, current_id)
                
                # 9. 间隔50ms，触发下一个ID的下载（不再传option）
                self.root.after(50, self.download_single_id)
            
            except Exception as e:
                self.log(f"❌ 下载 {current_id} 失败: {str(e)}", "ERROR")
                # 不要停止所有下载，继续下载下一个ID
                # 从输入框中移除失败的ID
                self.root.after(0, self.remove_downloaded_id, current_id)
                # 继续下载下一个ID
                self.root.after(50, self.download_single_id)
        
        thread = threading.Thread(target=download_thread_func, daemon=True)
        thread.start()
    
    def on_start_download_click(self):
        """开始下载按钮点击事件"""
        # 按钮点击动画
        if hasattr(self, 'download_btn'):
            self.button_press_animation(self.download_btn)
        
        # 调用开始下载方法（100ms后执行，避免动画卡顿）
        self.root.after(100, self.start_download)
    
    def clear_log(self):
        """清空日志"""
        self.log_text.text.delete(1.0, tk.END)
        self.log("日志已清空", "INFO")
    
    def open_url(self, url):
        """打开URL链接"""
        import webbrowser
        webbrowser.open(url)
    
    # 通用：将子组件的滚轮事件转发给Canvas，并执行滚动
    def _forward_wheel_event(self, event, target_canvas):
        """通用：将子组件的滚轮事件转发给Canvas，并执行滚动"""
        # 1. 先判断鼠标是否在Canvas范围内（避免影响其他组件）
        try:
            canvas_x = target_canvas.winfo_rootx()
            canvas_y = target_canvas.winfo_rooty()
            canvas_width = target_canvas.winfo_width()
            canvas_height = target_canvas.winfo_height()
            mouse_x = event.x_root - canvas_x
            mouse_y = event.y_root - canvas_y
            # 鼠标不在Canvas内，不处理
            if not (0 <= mouse_x <= canvas_width and 0 <= mouse_y <= canvas_height):
                return "break"
        except:
            # 如果获取信息失败，直接转发事件
            pass

        # 2. 统一判断滚动方向（和之前Canvas滚动逻辑一致）
        if event.delta:
            direction = -1 if event.delta > 0 else 1  # 上滚-1，下滚+1
        else:
            direction = -1 if event.num == 4 else 1  # Linux：4上滚，5下滚

        # 3. 执行Canvas滚动（和直接操作Canvas逻辑完全一致）
        target_canvas.yview_scroll(direction * self.scroll_units, "units")

        # 4. 阻止事件再传递，避免重复处理
        return "break"
    
    def on_show_cover_click(self):
        """显示封面按钮点击事件"""
        # 按钮点击动画
        cover_btn = None
        # 找到显示封面按钮
        for widget in self.root.winfo_children():
            if isinstance(widget, ttk.Frame):
                for child in widget.winfo_children():
                    if isinstance(child, ttk.Frame):
                        for subchild in child.winfo_children():
                            if isinstance(subchild, ttk.Frame):
                                for subsubchild in subchild.winfo_children():
                                    if isinstance(subsubchild, ttk.Frame):
                                        for btn in subsubchild.winfo_children():
                                            if isinstance(btn, ttk.Button) and btn.cget('text') == '显示封面':
                                                cover_btn = btn
                                                break
        
        if cover_btn:
            self.button_press_animation(cover_btn)
        
        # 调用显示封面方法
        self.root.after(100, self.show_cover)
    
    def remove_downloaded_id(self, downloaded_id):
        """删除输入框中已下载的ID，并重更新下载路径"""
        # 1. 获取当前输入框内容
        input_ids = self.album_id_entry.get().strip()
        if not input_ids:
            return
        
        # 2. 按逗号分割ID（处理"123,456,789"格式）
        id_list = [id.strip() for id in input_ids.split(',') if id.strip()]
        
        # 3. 删除已下载的ID（确保只删第一个匹配项，避免误删重复ID）
        if downloaded_id in id_list:
            id_list.remove(downloaded_id)
        
        # 4. 重新拼接ID（恢复"123,456"格式）
        new_input = ','.join(id_list) if id_list else ""
        
        # 5. 更新输入框内容
        self.album_id_entry.delete(0, tk.END)
        self.album_id_entry.insert(0, new_input)
        
        # 6. 实时更新下载路径（取新的第一个ID，无ID则用默认路径）
        self.update_download_path()
        self.log(f"已从输入框移除已下载ID: {downloaded_id}", "INFO")
        if new_input:
            self.log(f"当前剩余待下载ID: {new_input}", "INFO")
    
    def download_finished(self):
        """下载完成后的UI更新"""
        self.is_downloading = False
        self.stop_requested = False
        # 恢复按钮状态
        self.download_btn.config(state=tk.NORMAL)
        self.stop_btn.config(state=tk.DISABLED)
        # 移除进度条和状态标签的重置（因为组件已被移除）
        # self.progress_bar.config(value=0)
        # self.status_label.config(text="就绪", foreground='green')
    
    def stop_download(self):
        """停止下载"""
        if not self.is_downloading:
            messagebox.showinfo("提示", "当前没有下载任务在运行")
            return
        
        # 标记为停止，后续不再下载新ID
        self.stop_requested = True
        self.is_downloading = False
        self.log("⚠️ 用户已停止下载任务", "WARNING")
        self.log("💡 注意：当前正在下载的本子会继续完成，之后才会停止", "INFO")
        self.log("下载已取消，已下载的文件会保留", "INFO")
        
        # 立即恢复UI状态
        self.download_finished()
    
    def show_search_dialog(self):
        """显示搜索对话框"""
        search_text = self.search_entry.get().strip()
        if not search_text:
            messagebox.showwarning("警告", "请输入搜索关键词！")
            return
        
        # 创建搜索窗口
        search_window = tk.Toplevel(self.root)
        search_window.title(f"🔍 搜索结果: {search_text}")
        search_window.geometry("800x600")
        
        # 居中
        screen_width = search_window.winfo_screenwidth()
        screen_height = search_window.winfo_screenheight()
        x = (screen_width - 800) // 2
        y = (screen_height - 600) // 2
        search_window.geometry(f"+{x}+{y}")
        
        # 显示加载提示
        loading_label = ttk.Label(search_window, text="正在搜索...", font=('Arial', 14))
        loading_label.pack(pady=50)
        
        # 在后台线程中搜索
        def search_thread():
            try:

                
                option = JmOption.default()
                client = option.new_jm_client()
                
                # 搜索本子
                search_page = client.search_site(search_text)
                albums = list(search_page)
                
                # 更新UI
                self.root.after(0, lambda: self.display_search_results_final(search_window, albums, search_text))
                
            except Exception as e:
                self.log(f"搜索失败: {e}", "ERROR")
                self.root.after(0, lambda: messagebox.showerror("错误", f"搜索失败: {e}"))
                self.root.after(0, search_window.destroy)
        
        thread = threading.Thread(target=search_thread, daemon=True)
        thread.start()
    
    def display_search_results_final(self, window, results, search_text):
        """显示搜索结果 - 最终版本"""
        # 清空窗口
        for widget in window.winfo_children():
            widget.destroy()
        
        # 标题
        title_label = ttk.Label(window, text=f"搜索: {search_text}  (找到 {len(results)} 个结果)", 
                               font=('Arial', 12, 'bold'))
        title_label.pack(pady=10)
        
        # 创建滚动框架
        scroll_frame = ttk.Frame(window)
        scroll_frame.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)
        
        # 创建Canvas和滚动条
        canvas = tk.Canvas(scroll_frame, highlightthickness=0)
        scrollbar = ttk.Scrollbar(scroll_frame, orient="vertical", command=canvas.yview)
        scrollable_frame = ttk.Frame(canvas)
        
        # 配置滚动区域
        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )
        
        canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # 鼠标滚轮支持 - 兼容多平台
        def _on_mousewheel(event):
            # Windows和Mac
            if event.delta:
                canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
            # Linux
            else:
                if event.num == 4:
                    canvas.yview_scroll(-1, "units")
                elif event.num == 5:
                    canvas.yview_scroll(1, "units")
        
        # 只绑定Canvas的滚轮事件
        canvas.bind("<MouseWheel>", _on_mousewheel)  # Windows/Mac
        canvas.bind("<Button-4>", _on_mousewheel)    # Linux
        canvas.bind("<Button-5>", _on_mousewheel)    # Linux
        
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        
        # 显示结果
        for idx, item in enumerate(results[:50]):  # 最多显示50个
            # 解析搜索结果
            if isinstance(item, tuple) and len(item) >= 2:
                album_id, album_title = item[0], item[1]
            else:
                album_id = getattr(item, 'id', str(idx))
                album_title = getattr(item, 'title', str(item))
            
            # 创建结果项框架
            item_frame = ttk.Frame(scrollable_frame, relief=tk.RIDGE, borderwidth=1)
            item_frame.pack(fill=tk.X, padx=5, pady=2)
            
            # 给item_frame绑定滚轮事件转发
            item_frame.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            
            # 复选框
            var = tk.BooleanVar()
            
            def make_callback(album_id, current_var):  # 新增 current_var 参数绑定当前 var
                def on_change(*args):
                    self.update_album_id_entry(album_id, current_var.get())  # 使用绑定的 current_var
                return on_change
            
            # 绑定回调时传入当前 var
            var.trace('w', make_callback(album_id, var))
            cb = ttk.Checkbutton(item_frame, variable=var)
            cb.pack(side=tk.LEFT, padx=5, pady=5)
            
            # 信息文本
            info_text = f"ID: {album_id}  |  {album_title}"
            info_label = ttk.Label(item_frame, text=info_text, cursor='hand2')
            info_label.pack(side=tk.LEFT, padx=10, pady=5, fill=tk.X, expand=True)
            
            # 给info_label绑定滚轮事件转发
            info_label.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            
            # 点击标签也可以切换复选框
            info_label.bind('<Button-1>', lambda e, v=var: v.set(not v.get()))
        
        # 强制更新Canvas滚动区域（确保内容超出时可滚动）
        scrollable_frame.update_idletasks()  # 刷新布局
        canvas.configure(scrollregion=canvas.bbox("all"))  # 更新滚动范围
        
        # 确保窗口可以接收滚轮事件
        window.focus_set()
    
    def display_ranking_tab_final(self, scrollable_frame, albums, canvas):
        """显示排行榜内容 - 最终版本（修改为处理格式化后的字符串）"""
        # 清空框架
        for widget in scrollable_frame.winfo_children():
            widget.destroy()
        
        # 移除调试信息
        # self.log(f"显示排行榜数据，项目数量: {len(albums) if albums else 0}", "DEBUG")
        
        # 显示结果
        for idx, item in enumerate(albums[:50]):  # 最多显示50个
            # 检查数据类型
            if isinstance(item, str):
                # 如果是格式化后的字符串，直接使用
                info_text = item
                # 从格式化字符串中提取ID用于复选框功能
                # 格式: "ID: 1225833 |1| ［Vchan］我的合租女室友是不是过于淫荡了 9"
                try:
                    # 确保正确提取ID，避免title方法对象问题
                    parts = item.split("|")
                    if len(parts) >= 1:
                        album_id = parts[0].split(":")[1].strip()
                    else:
                        album_id = str(idx)
                except Exception as e:
                    album_id = str(idx)
                    self.log(f"解析排行榜项目ID失败: {e}", "WARNING")
            else:
                # 解析排行榜结果 - 确保正确处理从缓存加载的数据和从API获取的数据
                # 缓存数据格式: ["本子ID", "本子标题"]
                # API数据格式: ("本子ID", "本子标题")
                if isinstance(item, (list, tuple)) and len(item) >= 2:
                    # 从缓存加载的数据格式: [id, title] 或从API获取的数据格式: (id, title)
                    album_id, album_title = item[0], item[1]
                    # 确保album_title是字符串而不是方法对象
                    if hasattr(album_title, '__call__'):
                        # 如果是方法对象，调用它
                        album_title = album_title()
                    elif not isinstance(album_title, str):
                        # 如果不是字符串，转换为字符串
                        album_title = str(album_title)
                else:
                    # 其他格式的数据
                    album_id = getattr(item, 'id', str(idx))
                    album_title = getattr(item, 'title', str(item))
                    # 确保album_title是字符串而不是方法对象
                    if hasattr(album_title, '__call__'):
                        # 如果是方法对象，调用它
                        album_title = album_title()
                    elif not isinstance(album_title, str):
                        # 如果不是字符串，转换为字符串
                        album_title = str(album_title)
                
                # 信息文本 - 按照用户要求的格式: "ID: 本子ID | 排名 | 本子标题"
                # - 本子ID：来自缓存数据中的ID值（item[0]）
                # - 排名：数组索引+1
                # - 本子标题：来自缓存数据中的标题值（item[1]）
                album_id_str = str(album_id) if not isinstance(album_id, str) else album_id
                rank = idx + 1  # 排名（数组索引+1）
                info_text = f"ID: {album_id_str} |{rank}| {album_title}"
            
            # 移除调试信息
            # if idx < 3:
            #     self.log(f"排行榜项目 {idx+1}: ID={album_id}, Title={album_title}", "DEBUG")
            
            # 创建结果项框架
            item_frame = ttk.Frame(scrollable_frame, relief=tk.RIDGE, borderwidth=1)
            item_frame.pack(fill=tk.X, padx=5, pady=2)
            
            # 给item_frame绑定滚轮事件转发
            item_frame.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            
            # 复选框
            var = tk.BooleanVar()
            
            def make_callback(album_id, current_var):  # 新增 current_var 参数绑定当前 var
                def on_change(*args):
                    self.update_album_id_entry(album_id, current_var.get())  # 使用绑定的 current_var
                return on_change
            
            # 绑定回调时传入当前 var
            var.trace('w', make_callback(album_id, var))
            cb = ttk.Checkbutton(item_frame, variable=var)
            cb.pack(side=tk.LEFT, padx=5, pady=5)
            
            # 信息文本标签
            info_label = ttk.Label(item_frame, text=info_text, cursor='hand2')
            info_label.pack(side=tk.LEFT, padx=10, pady=5, fill=tk.X, expand=True)
            
            # 给info_label绑定滚轮事件转发
            info_label.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            
            # 点击标签也可以切换复选框
            info_label.bind('<Button-1>', lambda e, v=var: v.set(not v.get()))
        
        # 强制更新Canvas滚动区域（确保内容超出时可滚动）
        scrollable_frame.update_idletasks()  # 刷新布局
        canvas.configure(scrollregion=canvas.bbox("all"))  # 更新滚动范围
    
    def update_album_id_entry(self, album_id, is_checked):
        """更新本子ID输入框（新增：触发获取本子名字）"""
        current_ids = self.album_id_entry.get().strip()
        ids_list = [id.strip() for id in current_ids.split(',') if id.strip()] if current_ids else []
        
        if is_checked:  # 选中 - 添加ID
            if album_id not in ids_list:
                ids_list.append(album_id)
                # self.log(f"✅ 添加本子ID: {album_id}", "SUCCESS")  # 需删除/注释：去掉添加日志
        else:  # 取消选中 - 删除ID
            if album_id in ids_list:
                ids_list.remove(album_id)
                # self.log(f"↩️ 移除本子ID: {album_id}", "INFO")  # 需删除/注释：去掉移除日志
        
        # 更新输入框
        new_ids = ','.join(ids_list) if ids_list else ""
        self.album_id_entry.delete(0, tk.END)
        self.album_id_entry.insert(0, new_ids)
        
        # 1. 原有：更新下载路径
        self.update_download_path()
        
        # 2. 新增：触发获取本子名字（和手动输入逻辑完全一致）
        # 先取消之前的定时器（避免多次触发）
        if hasattr(self, '_fetch_timer'):
            self.root.after_cancel(self._fetch_timer)
        # 延迟800ms后获取名字（避免ID频繁变化导致的重复请求）
        self._fetch_timer = self.root.after(800, self.fetch_album_title_auto)
        
        # 记录当前输入框状态（需删除/注释）
        # if new_ids:
        #     self.log(f"📊 当前输入框ID列表: {new_ids}", "INFO")  # 需删除/注释：去掉列表日志
        # else:
        #     self.log(f"📊 输入框已清空", "INFO")  # 需删除/注释：去掉清空日志
    
    def show_ranking_dialog(self):
        """显示排行榜弹窗"""
        # 创建排行榜窗口
        ranking_window = tk.Toplevel(self.root)
        ranking_window.title("排行榜")
        ranking_window.geometry("900x700")
        
        # 居中
        screen_width = ranking_window.winfo_screenwidth()
        screen_height = ranking_window.winfo_screenheight()
        x = (screen_width - 900) // 2
        y = (screen_height - 700) // 2
        ranking_window.geometry(f"+{x}+{y}")
        
        # 标签页：日、周、月排行
        tab_control = ttk.Notebook(ranking_window)
        
        # 创建三个标签页
        day_tab = ttk.Frame(tab_control)
        week_tab = ttk.Frame(tab_control)
        month_tab = ttk.Frame(tab_control)
        
        tab_control.add(day_tab, text="📅 日排行")
        tab_control.add(week_tab, text="📆 周排行")
        tab_control.add(month_tab, text="📆 月排行")
        tab_control.pack(expand=1, fill=tk.BOTH, padx=10, pady=10)
        
        # 为每个标签页创建排行榜内容
        self.create_ranking_tab_final(day_tab, 'day')
        self.create_ranking_tab_final(week_tab, 'week')
        self.create_ranking_tab_final(month_tab, 'month')
    


    def create_ranking_tab_final(self, parent, time_type):
        """创建排行榜标签页内容 - 最终版本（带缓存功能和改进的滚动）"""
        import time
        
        # 创建滚动框架
        scroll_frame = ttk.Frame(parent)
        scroll_frame.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)
        
        # 创建Canvas和滚动条
        canvas = tk.Canvas(scroll_frame, highlightthickness=0)
        scrollbar = ttk.Scrollbar(scroll_frame, orient="vertical", command=canvas.yview)
        scrollable_frame = ttk.Frame(canvas)
        
        # 配置滚动区域
        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )
        
        canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # 鼠标滚轮支持 - 兼容多平台
        def _on_mousewheel(event):
            # Windows和Mac
            if event.delta:
                canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
            # Linux
            else:
                if event.num == 4:
                    canvas.yview_scroll(-1, "units")
                elif event.num == 5:
                    canvas.yview_scroll(1, "units")
            # 关键：阻止事件继续传播给子组件（避免被拦截）
            return "break"
        
        # 只绑定Canvas的滚轮事件
        canvas.bind("<MouseWheel>", _on_mousewheel)  # Windows/Mac
        canvas.bind("<Button-4>", _on_mousewheel)    # Linux
        canvas.bind("<Button-5>", _on_mousewheel)    # Linux
        
        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        
        # 检查缓存
        current_time = time.time()
        # 检查是否是同一天且缓存存在
        cache_valid = (
            self.ranking_cache[time_type] is not None
            # 不再检查时间戳是否是同一天，因为我们已经在启动时清理了过期缓存
            # 只要缓存存在就使用它
        )
        
        if cache_valid:
            # 使用缓存数据
            loading_label = ttk.Label(scrollable_frame, text="正在加载缓存数据...", font=('Arial', 14))
            loading_label.pack(pady=50)
            self.root.after(100, lambda: self.display_ranking_tab_final(scrollable_frame, self.ranking_cache[time_type], canvas))
        else:
            # 加载提示
            loading_label = ttk.Label(scrollable_frame, text="正在加载排行榜...", font=('Arial', 14))
            loading_label.pack(pady=50)
            
            # 在后台线程中加载排行榜
            def load_thread():
                try:
                    # 移除重复导入
                    option = JmOption.default()
                    client = option.new_jm_client()
                    
                    # 根据类型获取排行榜
                    ranking_page = None
                    try:
                        # 尝试使用新版API
                        if time_type == 'day':
                            ranking_page = client.day_ranking(page=1)
                        elif time_type == 'week':
                            ranking_page = client.week_ranking(page=1)
                        else:  # month
                            ranking_page = client.month_ranking(page=1)
                    except AttributeError as e1:
                        try:
                            # 兼容旧版API
                            if time_type == 'day':
                                ranking_page = client.ranking('day', page=1)
                            elif time_type == 'week':
                                ranking_page = client.ranking('week', page=1)
                            else:  # month
                                ranking_page = client.ranking('month', page=1)
                        except Exception as e2:
                            # 尝试另一种可能的API格式
                            try:
                                if time_type == 'day':
                                    ranking_page = client.ranking('day_ranking', page=1)
                                elif time_type == 'week':
                                    ranking_page = client.ranking('week_ranking', page=1)
                                else:  # month
                                    ranking_page = client.ranking('month_ranking', page=1)
                            except Exception as e3:
                                # 记录所有错误信息
                                error_msg = f"无法获取{time_type}排行榜: {e1}, {e2}, {e3}"
                                self.log(error_msg, "ERROR")
                                self.root.after(0, lambda: loading_label.config(
                                    text=error_msg, foreground='red'
                                ))
                                return
                    
                    if ranking_page is None:
                        error_msg = f"无法获取{time_type}排行榜: ranking_page为None"
                        self.log(error_msg, "ERROR")
                        self.root.after(0, lambda: loading_label.config(
                            text=error_msg, foreground='red'
                        ))
                        return
                    
                    # 转换为列表
                    try:
                        albums = list(ranking_page)
                    except Exception as e:
                        error_msg = f"无法转换{time_type}排行榜数据: {e}"
                        self.log(error_msg, "ERROR")
                        self.root.after(0, lambda: loading_label.config(
                            text=error_msg, foreground='red'
                        ))
                        return
                    
                    # 检查数据是否为空
                    if not albums:
                        error_msg = f"{time_type}排行榜数据为空"
                        self.log(error_msg, "INFO")
                        self.root.after(0, lambda: loading_label.config(
                            text=error_msg, foreground='orange'
                        ))
                        return
                    
                    # 保存到缓存
                    self.ranking_cache[time_type] = albums
                    self.ranking_cache_time[time_type] = current_time
                    
                    # 更新UI
                    self.root.after(0, lambda: self.display_ranking_tab_final(scrollable_frame, albums, canvas))
                    
                except Exception as e:
                    import traceback
                    error_msg = f"加载{time_type}排行榜失败: {e}\n{traceback.format_exc()}"
                    self.log(error_msg, "ERROR")
                    self.root.after(0, lambda: loading_label.config(
                        text=f"加载失败: {e}", foreground='red'
                    ))
            
            thread = threading.Thread(target=load_thread, daemon=True)
            thread.start()
    
    def _is_same_day(self, timestamp1, timestamp2):
        """检查两个时间戳是否是同一天"""
        import datetime
        date1 = datetime.datetime.fromtimestamp(timestamp1).date()
        date2 = datetime.datetime.fromtimestamp(timestamp2).date()
        return date1 == date2
    
    
    def save_ranking_cache(self):
        """保存排行榜缓存（不再保存到文件，只保持内存缓存）"""
        # 不再保存到文件，只保持内存缓存
        pass
    
    def load_ranking_cache(self):
        """加载排行榜缓存（不再从文件加载，只使用内存缓存）"""
        # 不再从文件加载，只使用内存中的数据
        # 程序启动时 ranking_cache 已经初始化为空缓存
        pass
    
    def clear_ranking_cache(self):
        """清除排行榜缓存"""
        self.ranking_cache = {
            'day': None,
            'week': None,
            'month': None
        }
        self.ranking_cache_time = {
            'day': 0,
            'week': 0,
            'month': 0
        }
        # 不再清除文件缓存，只清除内存缓存
    
    def on_closing(self):
        """程序关闭时的处理"""
        # 不再保存排行榜缓存到文件
        # 关闭主窗口
        self.root.destroy()
    

    


def main():
    """主函数"""
    # 使用 ttkbootstrap 创建现代化的窗口
    root = ttk.Window(themename="flatly")  # flatly = 浅色 Windows 11 风格
    app = JMComicGUI(root)
    
    # 设置窗口图标（如果有的话）
    try:
        root.iconbitmap('icon.ico')
    except:
        pass
    
    # 居中显示窗口
    root.place_window_center()
    
    root.mainloop()


if __name__ == "__main__":
    main()
