import tkinter as tk
from tkinter import messagebox, filedialog
import ttkbootstrap as ttk
from ttkbootstrap.constants import *
from ttkbootstrap.scrolled import ScrolledText
import threading
import os
import sys
import jmcomic
from pathlib import Path
from PIL import Image, ImageTk
import requests
from io import BytesIO
import json
import datetime
import time
import webbrowser
import tempfile
import shutil

class JMComicGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("JMComic 下载器 v1.1")
        self.root.geometry("1326x933")
        self.root.resizable(True, True)

        self.is_downloading = False
        self.stop_requested = False
        self.option = None
        self.current_album_info = None
        self.current_cover_album_id = None

        self.scroll_units = 1

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

        self.create_widgets()
        self.load_default_config()

    def _get_end_of_day_timestamp(self):
        today = datetime.date.today()
        tomorrow = today + datetime.timedelta(days=1)
        tomorrow_timestamp = time.mktime(tomorrow.timetuple())
        return tomorrow_timestamp

    def log(self, content, level="INFO"):
        time_stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        log_content = f"[{time_stamp}][{level}] {content}"
        self.log_text.text.insert(tk.END, log_content + "\n", level)
        self.log_text.text.see(tk.END)
        self.root.update_idletasks()

    def fade_in_window(self, window, duration=200):
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

                window.geometry(f"{current_width}x{current_height}+{x + offset_x}+{y + offset_y}")
                window.after(step_time, lambda: animate(step + 1))
            else:
                window.geometry(f"{width}x{height}+{x}+{y}")

        animate()

    def button_press_animation(self, button):
        original_style = button.cget('style') if hasattr(button, 'cget') else None

        try:
            button.state(['pressed'])
        except:
            pass

        def restore():
            try:
                button.state(['!pressed'])
            except:
                pass

        self.root.after(100, restore)

    def create_widgets(self):
        main_container = ttk.Frame(self.root, padding="10")
        main_container.grid(row=0, column=0, sticky=tk.W + tk.E + tk.N + tk.S)

        self.root.protocol("WM_DELETE_WINDOW", self.on_closing)

        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(0, weight=1)
        main_container.columnconfigure(0, weight=1)
        main_container.columnconfigure(1, weight=0)
        main_container.rowconfigure(0, weight=1)

        left_frame = ttk.Frame(main_container)
        left_frame.grid(row=0, column=0, sticky=tk.W + tk.E + tk.N + tk.S, padx=(0, 10))
        left_frame.columnconfigure(0, weight=1)
        left_frame.rowconfigure(5, weight=1)

        right_frame = ttk.Frame(main_container)
        right_frame.grid(row=0, column=1, sticky=tk.N, padx=0)

        cover_frame = ttk.LabelFrame(right_frame, text="📷 封面预览", padding="10")
        cover_frame.pack(fill=tk.BOTH, expand=False)

        self.cover_label = ttk.Label(cover_frame, text="暂无封面\n\n请输入本子ID\n并点击'显示封面'",
                                     foreground='gray', anchor='center', justify='center',
                                     width=25, cursor='hand2')
        self.cover_label.pack(pady=5)
        self.current_cover_image = None
        self.full_cover_image = None

        self.cover_label.bind('<Button-1>', self.show_full_cover)

        info_frame = ttk.LabelFrame(right_frame, text="📝 关于", padding="10")
        info_frame.pack(fill=tk.BOTH, expand=False, pady=(10, 0))

        info_text = ttk.Label(info_frame, text="作者: yxxawa\n\n版本: 1.1\n\n蕉♂流群: 21013274471\n\nAPI项目:",
                              foreground='gray', font=('Arial', 9), justify='left')
        info_text.pack(anchor='w')

        api_link = ttk.Label(info_frame, text="github.com/hect0x7/\nJMComic-Crawler-Python",
                             foreground='blue', font=('Arial', 8), justify='left', cursor='hand2')
        api_link.pack(anchor='w', padx=(0, 0))
        api_link.bind('<Button-1>', lambda e: self.open_url('https://github.com/hect0x7/JMComic-Crawler-Python'))

        search_frame = ttk.Frame(left_frame)
        search_frame.grid(row=0, column=0, sticky=tk.W + tk.E, pady=(0, 5))
        search_frame.columnconfigure(1, weight=1)

        ttk.Label(search_frame, text="🔍 搜索:", font=('Arial', 10)).grid(row=0, column=0, sticky=tk.W, padx=(0, 5))
        self.search_entry = ttk.Entry(search_frame)
        self.search_entry.grid(row=0, column=1, sticky=tk.W + tk.E, padx=5)
        self.search_entry.bind('<Return>', lambda e: self.show_search_dialog())

        self.search_placeholder = "快来搜索你想要的本子吧^_^"
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

        self.search_entry.bind('<FocusIn>', on_search_focus_in)
        self.search_entry.bind('<FocusOut>', on_search_focus_out)

        ttk.Button(search_frame, text="搜索", command=self.on_search_button_click, width=10).grid(row=0, column=2,
                                                                                                  padx=5)

        title_label = ttk.Label(left_frame, text="🚀 JMComic 漫画下载器",
                                font=('Arial', 16, 'bold'))
        title_label.grid(row=1, column=0, pady=10, sticky=tk.W)

        input_frame = ttk.LabelFrame(left_frame, text="📥 下载设置", padding="10")
        input_frame.grid(row=2, column=0, sticky=tk.W + tk.E, pady=5)
        input_frame.columnconfigure(1, weight=1)

        ttk.Label(input_frame, text="本子ID:").grid(row=0, column=0, sticky=tk.W, pady=5)

        id_frame = ttk.Frame(input_frame)
        id_frame.grid(row=0, column=1, sticky=tk.W + tk.E, padx=5, pady=5)
        id_frame.columnconfigure(0, weight=0)
        id_frame.columnconfigure(1, weight=1)

        self.album_id_entry = ttk.Entry(id_frame, width=15)
        self.album_id_entry.grid(row=0, column=0, sticky=tk.W)

        self.album_id_entry.bind('<KeyRelease>', self.on_id_input_change)

        self.album_title_label = ttk.Label(id_frame, text="", foreground='gray', font=('Arial', 9))
        self.album_title_label.grid(row=0, column=1, sticky=tk.W, padx=(5, 0))

        ttk.Button(id_frame, text="显示封面", command=self.on_show_cover_click,
                   width=10).grid(row=0, column=2, padx=(10, 0))

        ttk.Label(input_frame, text="提示: 可输入多个ID，用空格或逗号分隔",
                  font=('Arial', 8), foreground='gray').grid(row=1, column=1, sticky=tk.W, padx=5)

        ttk.Label(input_frame, text="保存路径:").grid(row=2, column=0, sticky=tk.W, pady=5)
        path_frame = ttk.Frame(input_frame)
        path_frame.grid(row=2, column=1, sticky=tk.W + tk.E, pady=5)
        path_frame.columnconfigure(0, weight=1)

        self.download_path_entry = ttk.Entry(path_frame)
        self.download_path_entry.grid(row=0, column=0, sticky=tk.W + tk.E, padx=(0, 5))
        if getattr(sys, 'frozen', False):
            exe_dir = Path(sys.executable).parent
        else:
            exe_dir = Path(__file__).parent
        default_path = exe_dir / "JMDownLoad"
        self.download_path_entry.insert(0, str(default_path))

        ttk.Button(path_frame, text="浏览...", command=self.browse_folder,
                   width=10).grid(row=0, column=1)

        options_frame = ttk.LabelFrame(left_frame, text="⚙️ 高级选项", padding="10")
        options_frame.grid(row=3, column=0, sticky=tk.W + tk.E, pady=5)
        options_frame.columnconfigure(1, weight=1)

        ttk.Label(options_frame, text="图片格式:").grid(row=0, column=0, sticky=tk.W, pady=5)
        self.image_format_var = tk.StringVar(value=".png")
        format_combo = ttk.Combobox(options_frame, textvariable=self.image_format_var,
                                    values=["原始格式", ".png", ".jpg", ".webp"],
                                    state="readonly", width=15)
        format_combo.grid(row=0, column=1, sticky=tk.W, padx=5, pady=5)

        ttk.Label(options_frame, text="并发章节数:").grid(row=0, column=2, sticky=tk.W, pady=5, padx=(20, 0))
        self.thread_count_var = tk.IntVar(value=1)
        thread_spin = ttk.Spinbox(options_frame, from_=1, to=5,
                                  textvariable=self.thread_count_var, width=10)
        thread_spin.grid(row=0, column=3, sticky=tk.W, padx=5, pady=5)

        ttk.Label(options_frame, text="并发图片数:").grid(row=1, column=0, sticky=tk.W, pady=5)
        self.image_thread_var = tk.IntVar(value=5)
        image_thread_spin = ttk.Spinbox(options_frame, from_=1, to=20,
                                        textvariable=self.image_thread_var, width=10)
        image_thread_spin.grid(row=1, column=1, sticky=tk.W, padx=5, pady=5)

        button_frame = ttk.Frame(left_frame)
        button_frame.grid(row=4, column=0, pady=10)

        self.download_btn = ttk.Button(button_frame, text="🚀 开始下载",
                                       command=self.on_start_download_click, width=15)
        self.download_btn.grid(row=0, column=0, padx=5)

        self.stop_btn = ttk.Button(button_frame, text="⏹️ 停止下载",
                                   command=self.stop_download, width=15, state=tk.DISABLED)
        self.stop_btn.grid(row=0, column=1, padx=5)

        ttk.Button(button_frame, text="📁 打开下载目录",
                   command=self.open_download_folder, width=15).grid(row=0, column=2, padx=5)

        ttk.Button(button_frame, text="🔄 清空日志",
                   command=self.clear_log, width=15).grid(row=0, column=3, padx=5)

        log_frame = ttk.LabelFrame(left_frame, text="📋 下载日志", padding="5")
        log_frame.grid(row=5, column=0, sticky=tk.W + tk.E + tk.N + tk.S, pady=5)
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)

        self.log_text = ScrolledText(log_frame, height=12, wrap=tk.WORD, autohide=True)
        self.log_text.pack(fill=tk.BOTH, expand=True)

        self.log_text.text.tag_config('INFO', foreground='black')
        self.log_text.text.tag_config('SUCCESS', foreground='green')
        self.log_text.text.tag_config('ERROR', foreground='red')
        self.log_text.text.tag_config('WARNING', foreground='orange')

        footer_frame = ttk.Frame(left_frame)
        footer_frame.grid(row=7, column=0, sticky=tk.W + tk.E, pady=5)
        footer_frame.columnconfigure(1, weight=1)

        ttk.Label(footer_frame, text="💡 使用提示: 支持下载本子ID(如422866)或章节ID(如p456)，多个ID用空格或逗号分隔",
                  font=('Arial', 8)).grid(row=0, column=0, sticky=tk.W, padx=10)

        buttons_frame = ttk.Frame(footer_frame)
        buttons_frame.grid(row=0, column=1, padx=0)

        ranking_btn = ttk.Button(buttons_frame, text="🏆 查看排行榜",
                                 command=self.on_ranking_button_click, width=15)
        ranking_btn.pack(side=tk.LEFT, padx=(0, 5))

    def on_id_input_change(self, event):
        self.update_download_path()
        if hasattr(self, '_fetch_timer'):
            self.root.after_cancel(self._fetch_timer)
        self._fetch_timer = self.root.after(800, self.fetch_album_title_auto)

    def update_download_path(self):
        album_id = self.album_id_entry.get().strip()

        if getattr(sys, 'frozen', False):
            exe_dir = Path(sys.executable).parent
        else:
            exe_dir = Path(__file__).parent

        if not album_id:
            default_path = exe_dir / "JMDownLoad"
            self.download_path_entry.delete(0, tk.END)
            self.download_path_entry.insert(0, str(default_path))
            return

        ids = self.parse_ids(album_id)
        if not ids:
            return

        first_id = ids[0]

        if first_id.lower().startswith('p'):
            first_id = first_id[1:]

        new_path = exe_dir / "JMDownLoad" / first_id
        self.download_path_entry.delete(0, tk.END)
        self.download_path_entry.insert(0, str(new_path))

    def fetch_album_title_auto(self):
        album_id = self.album_id_entry.get().strip()

        if not album_id:
            self.album_title_label.config(text="")
            self.current_album_info = None
            return

        if ' ' in album_id or ',' in album_id:
            self.album_title_label.config(text="--- [仅显示单个ID的名称]", foreground='gray')
            self.current_album_info = None
            return

        if album_id.lower().startswith('p'):
            self.album_title_label.config(text="--- [章节ID]", foreground='gray')
            self.current_album_info = None
            return

        def fetch_thread():
            try:
                from jmcomic import JmOption
                temp_option = JmOption.default()

                try:
                    client = temp_option.build_jm_client()
                    album = client.get_album_detail(album_id)
                except (AttributeError, ImportError):
                    album = None

                if album and hasattr(album, 'title'):
                    title = album.title
                    self.current_album_info = album
                    self.root.after(0, lambda: self.album_title_label.config(
                        text=f" --- [{title}]", foreground='darkgray'
                    ))
                else:
                    self.current_album_info = None
                    self.root.after(0, lambda: self.album_title_label.config(
                        text="", foreground='gray'
                    ))

            except Exception as e:
                self.current_album_info = None
                self.root.after(0, lambda: self.album_title_label.config(text="", foreground='gray'))

        thread = threading.Thread(target=fetch_thread, daemon=True)
        thread.start()

    def show_cover(self, album_id=None):
        if album_id is None:
            album_id = self.album_id_entry.get().strip()

        if not album_id:
            messagebox.showwarning("警告", "请先输入本子ID！")
            return

        ids = self.parse_ids(album_id)
        if not ids:
            messagebox.showwarning("警告", "请输入有效的本子ID！")
            return

        first_id = ids[0]

        if first_id.lower().startswith('p'):
            messagebox.showwarning("警告", "章节ID没有封面，请输入本子ID！")
            return

        if self.current_cover_album_id == first_id:
            self.log(f"封面已经是 ID {first_id} 的封面，无需重复获取", "INFO")
            return

        def fetch_cover_thread():
            self.log(f"开始获取封面: {first_id}", "INFO")
            self.root.after(0, lambda: self.cover_label.config(text="正在加载封面...", foreground='blue'))

            import jmcomic
            from jmcomic import JmOption

            temp_option = JmOption.default()
            self.log("正在创建客户端...", "INFO")

            try:
                client = temp_option.new_jm_client()
                self.log("客户端创建成功", "INFO")

                temp_dir = tempfile.mkdtemp()

                try:
                    temp_file = os.path.join(temp_dir, "cover.jpg")

                    self.log("正在下载封面图片...", "INFO")
                    client.download_album_cover(first_id, temp_file)

                    self.log("正在处理图片...", "INFO")
                    with open(temp_file, 'rb') as f:
                        image_data = f.read()

                    image = Image.open(BytesIO(image_data))

                    self.full_cover_image = image.copy()

                    target_width = 200
                    ratio = target_width / image.width
                    new_height = int(image.height * ratio)
                    if new_height > 280:
                        new_height = 280
                        ratio = new_height / image.height
                        target_width = int(image.width * ratio)

                    image = image.resize((target_width, new_height), Image.Resampling.LANCZOS)

                    photo_img = ImageTk.PhotoImage(image)

                    self.root.after(0, lambda: self.update_cover_display(photo_img, first_id))
                    self.log("✅ 封面加载成功！点击封面可放大查看", "SUCCESS")

                finally:
                    import shutil
                    if os.path.exists(temp_dir):
                        shutil.rmtree(temp_dir)

            except Exception as e:
                self.log(f"下载封面失败: {e}", "ERROR")
                import traceback
                error_detail = traceback.format_exc()
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
        self.current_cover_image = photo
        self.cover_label.config(image=photo, text="", compound='image')
        if album_id:
            self.current_cover_album_id = album_id

    def show_full_cover(self, event=None):
        if self.full_cover_image is None:
            return

        cover_window = tk.Toplevel(self.root)
        cover_window.title("📷 封面查看")

        screen_width = cover_window.winfo_screenwidth()
        screen_height = cover_window.winfo_screenheight()

        img_width = self.full_cover_image.width
        img_height = self.full_cover_image.height

        max_width = int(screen_width * 0.8)
        max_height = int(screen_height * 0.8)

        scale = min(max_width / img_width, max_height / img_height, 1.0)
        new_width = int(img_width * scale)
        new_height = int(img_height * scale)

        display_image = self.full_cover_image.resize((new_width, new_height), Image.Resampling.LANCZOS)
        photo = ImageTk.PhotoImage(display_image)

        cover_window.geometry(f"{new_width + 40}x{new_height + 80}")

        x = (screen_width - new_width - 40) // 2
        y = (screen_height - new_height - 80) // 2
        cover_window.geometry(f"+{x}+{y}")

        image_label = tk.Label(cover_window, image=photo)
        image_label.image = photo
        image_label.pack(padx=20, pady=20)

        tip_label = tk.Label(cover_window,
                             text=f"📊 原始尺寸: {img_width}x{img_height}  |  显示尺寸: {new_width}x{new_height}",
                             font=('Arial', 9), fg='gray')
        tip_label.pack(pady=(0, 10))

        image_label.bind('<Button-1>', lambda e: cover_window.destroy())

        cover_window.bind('<Escape>', lambda e: cover_window.destroy())

        cover_window.transient(self.root)
        cover_window.grab_set()

    def fetch_album_title(self):
        album_id = self.album_id_entry.get().strip()

        self.album_title_label.config(text="")

        if not album_id:
            return

        ids = self.parse_ids(album_id)
        if not ids:
            return

        first_id = ids[0]

        if first_id.lower().startswith('p'):
            self.album_title_label.config(text="---- [章节ID]")
            return

        def fetch_thread():
            try:
                self.album_title_label.config(text="---- [正在获取...]")
                self.root.update_idletasks()

                import jmcomic
                from jmcomic import JmOption

                temp_option = JmOption.default()

                try:
                    client = temp_option.new_jm_client()
                    album = client.get_album_detail(first_id)
                except AttributeError:
                    album = None

                if album and hasattr(album, 'title'):
                    title = album.title
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

        thread = threading.Thread(target=fetch_thread, daemon=True)
        thread.start()

    def load_default_config(self):
        self.log("系统初始化完成，准备就绪！", "SUCCESS")

    def on_search_button_click(self):
        search_btn = None
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

        self.root.after(100, self.show_search_dialog)

    def on_ranking_button_click(self):
        ranking_btn = None
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

        self.root.after(100, self.show_ranking_dialog)

    def browse_folder(self):
        folder = filedialog.askdirectory()
        if folder:
            self.download_path_entry.delete(0, tk.END)
            self.download_path_entry.insert(0, folder)

    def open_download_folder(self):
        import os

        current_path = self.download_path_entry.get().strip()
        if not current_path:
            messagebox.showerror("错误", "下载路径为空！请先设置保存路径")
            return

        current_path = os.path.abspath(current_path)

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

        try:
            if os.name == 'nt':
                os.startfile(current_path)
            else:
                webbrowser.open(current_path)
            self.log(f"✅ 已打开下载文件夹：{current_path}", "INFO")
        except Exception as e:
            messagebox.showerror("打开失败", f"无法打开文件夹：{str(e)}")
            self.log(f"❌ 打开文件夹失败：{str(e)}", "ERROR")

    def create_option(self):
        try:
            import jmcomic
            from jmcomic import JmOption
            import os

            current_download_path = self.download_path_entry.get().strip()
            if not current_download_path:
                messagebox.showerror("错误", "下载路径不能为空！")
                return None

            current_download_path = os.path.abspath(current_download_path)
            if not os.path.exists(current_download_path):
                os.makedirs(current_download_path, exist_ok=True)

            selected_format = self.image_format_var.get()
            if selected_format == "原始格式":
                image_suffix = None
            else:
                image_suffix = selected_format

            option = JmOption(
                dir_rule={
                    "rule": "Bd_Pname",
                    "base_dir": current_download_path
                },
                download={
                    "cache": True,
                    "image": {
                        "decode": True,
                        "suffix": image_suffix
                    },
                    "threading": {
                        "image": 30,
                        "photo": 24,
                        "max_workers": 3
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
        if not input_str:
            return []
        id_list = []
        for part in input_str.split(','):
            id_list.extend(part.split())
        return [id.strip() for id in id_list if id.strip()]

    def start_download(self):
        if self.is_downloading:
            messagebox.showwarning("警告", "已有下载任务在进行中！")
            return

        input_ids = self.album_id_entry.get().strip()
        if not input_ids:
            messagebox.showwarning("警告", "请输入要下载的本子ID或章节ID！")
            return

        ids = self.parse_ids(input_ids)
        unique_ids = []
        for id in ids:
            if id not in unique_ids:
                unique_ids.append(id)
        if not unique_ids:
            messagebox.showwarning("警告", "没有有效的ID！")
            return

        self.stop_requested = False
        self.is_downloading = True
        self.download_btn.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)

        self.root.after(0, self.download_single_id)

    def download_task(self, ids, option):
        try:
            self.log(f"开始下载任务，共 {len(ids)} 个ID", "INFO")
            self.log(f"下载路径: {option.dir_rule.base_dir}", "INFO")
            self.log("-" * 60, "INFO")

            for idx, item_id in enumerate(ids, 1):
                if self.stop_requested:
                    self.log("下载已被用户取消", "WARNING")
                    break

                if not self.is_downloading:
                    self.log("下载已被用户取消", "WARNING")
                    break

                try:
                    self.log(f"[{idx}/{len(ids)}] 开始下载: {item_id}", "INFO")

                    if item_id.lower().startswith('p'):
                        photo_id = item_id[1:]
                        jmcomic.download_photo(photo_id, option)
                        self.log(f"✅ 章节 {item_id} 下载完成", "SUCCESS")
                    else:
                        jmcomic.download_album(item_id, option)
                        self.log(f"✅ 本子 {item_id} 下载完成", "SUCCESS")

                except Exception as e:
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
            self.root.after(0, self.download_finished)

    def download_single_id(self):
        if self.stop_requested or not self.is_downloading:
            self.log("下载任务已停止（用户取消或无更多ID）", "WARNING")
            self.download_finished()
            return

        input_ids = self.album_id_entry.get().strip()
        if not input_ids:
            self.log("所有ID下载完成！", "SUCCESS")
            self.download_finished()
            return

        ids = self.parse_ids(input_ids)
        current_id = ids[0]
        total_remaining = len(ids)

        option = self.create_option()
        if option is None:
            self.log("创建下载配置失败，停止当前ID下载", "ERROR")
            self.root.after(50, self.download_single_id)
            return

        if not current_id.lower().startswith('p') and self.current_cover_album_id != current_id:
            self.log(f"自动获取封面: {current_id}", "INFO")
            self.show_cover(current_id)

        def progress_callback(current, total, info):
            if self.stop_requested:
                self.log(f"⚠️ 检测到停止请求，当前下载完成后将停止", "WARNING")

        option.progress_callback = progress_callback

        def download_thread_func():
            try:
                self.log(f"开始下载（剩余{total_remaining}个）: {current_id}", "INFO")
                self.log(f"当前ID下载路径: {option.dir_rule.base_dir}", "INFO")

                if current_id.lower().startswith('p'):
                    photo_id = current_id[1:]
                    jmcomic.download_photo(photo_id, option)
                    self.log(f"✅ 章节 {current_id} 下载完成（路径：{option.dir_rule.base_dir}）", "SUCCESS")
                else:
                    jmcomic.download_album(current_id, option)
                    self.log(f"✅ 本子 {current_id} 下载完成（路径：{option.dir_rule.base_dir}）", "SUCCESS")

                self.root.after(0, self.remove_downloaded_id, current_id)

                self.root.after(50, self.download_single_id)

            except Exception as e:
                self.log(f"❌ 下载 {current_id} 失败: {str(e)}", "ERROR")
                self.stop_requested = True
                self.root.after(0, self.download_finished)

        thread = threading.Thread(target=download_thread_func, daemon=True)
        thread.start()

    def on_start_download_click(self):
        if hasattr(self, 'download_btn'):
            self.button_press_animation(self.download_btn)

        self.root.after(100, self.start_download)

    def clear_log(self):
        self.log_text.text.delete(1.0, tk.END)
        self.log("日志已清空", "INFO")

    def open_url(self, url):
        webbrowser.open(url)

    def _forward_wheel_event(self, event, target_canvas):
        try:
            canvas_x = target_canvas.winfo_rootx()
            canvas_y = target_canvas.winfo_rooty()
            canvas_width = target_canvas.winfo_width()
            canvas_height = target_canvas.winfo_height()
            mouse_x = event.x_root - canvas_x
            mouse_y = event.y_root - canvas_y
            if not (0 <= mouse_x <= canvas_width and 0 <= mouse_y <= canvas_height):
                return "break"
        except:
            pass

        if event.delta:
            direction = -1 if event.delta > 0 else 1
        else:
            direction = -1 if event.num == 4 else 1

        target_canvas.yview_scroll(direction * self.scroll_units, "units")

        return "break"

    def on_show_cover_click(self):
        cover_btn = None
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

        self.root.after(100, self.show_cover)

    def remove_downloaded_id(self, downloaded_id):
        input_ids = self.album_id_entry.get().strip()
        if not input_ids:
            return

        id_list = [id.strip() for id in input_ids.split(',') if id.strip()]

        if downloaded_id in id_list:
            id_list.remove(downloaded_id)

        new_input = ','.join(id_list) if id_list else ""

        self.album_id_entry.delete(0, tk.END)
        self.album_id_entry.insert(0, new_input)

        self.update_download_path()
        self.log(f"已从输入框移除已下载ID: {downloaded_id}", "INFO")
        if new_input:
            self.log(f"当前剩余待下载ID: {new_input}", "INFO")

    def download_finished(self):
        self.is_downloading = False
        self.stop_requested = False
        self.download_btn.config(state=tk.NORMAL)
        self.stop_btn.config(state=tk.DISABLED)

    def stop_download(self):
        if not self.is_downloading:
            messagebox.showinfo("提示", "当前没有下载任务在运行")
            return

        self.stop_requested = True
        self.is_downloading = False
        self.log("⚠️ 用户已停止下载任务", "WARNING")
        self.log("💡 注意：当前正在下载的本子会继续完成，之后才会停止", "INFO")
        self.log("下载已取消，已下载的文件会保留", "INFO")

        self.download_finished()

    def show_search_dialog(self):
        search_text = self.search_entry.get().strip()
        if not search_text:
            messagebox.showwarning("警告", "请输入搜索关键词！")
            return

        search_window = tk.Toplevel(self.root)
        search_window.title(f"🔍 搜索结果: {search_text}")
        search_window.geometry("800x600")

        screen_width = search_window.winfo_screenwidth()
        screen_height = search_window.winfo_screenheight()
        x = (screen_width - 800) // 2
        y = (screen_height - 600) // 2
        search_window.geometry(f"+{x}+{y}")

        loading_label = ttk.Label(search_window, text="正在搜索...", font=('Arial', 14))
        loading_label.pack(pady=50)

        def search_thread():
            try:
                import jmcomic
                from jmcomic import JmOption

                option = JmOption.default()
                client = option.new_jm_client()

                search_page = client.search_site(search_text)
                albums = list(search_page)

                self.root.after(0, lambda: self.display_search_results_final(search_window, albums, search_text))

            except Exception as e:
                self.log(f"搜索失败: {e}", "ERROR")
                self.root.after(0, lambda: messagebox.showerror("错误", f"搜索失败: {e}"))
                self.root.after(0, search_window.destroy)

        thread = threading.Thread(target=search_thread, daemon=True)
        thread.start()

    def display_search_results_final(self, window, results, search_text):
        for widget in window.winfo_children():
            widget.destroy()

        title_label = ttk.Label(window, text=f"搜索: {search_text}  (找到 {len(results)} 个结果)",
                                font=('Arial', 12, 'bold'))
        title_label.pack(pady=10)

        scroll_frame = ttk.Frame(window)
        scroll_frame.pack(fill=tk.BOTH, expand=True, padx=10, pady=10)

        canvas = tk.Canvas(scroll_frame, highlightthickness=0)
        scrollbar = ttk.Scrollbar(scroll_frame, orient="vertical", command=canvas.yview)
        scrollable_frame = ttk.Frame(canvas)

        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )

        canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)

        def _on_mousewheel(event):
            if event.delta:
                canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
            else:
                if event.num == 4:
                    canvas.yview_scroll(-1, "units")
                elif event.num == 5:
                    canvas.yview_scroll(1, "units")

        canvas.bind("<MouseWheel>", _on_mousewheel)
        canvas.bind("<Button-4>", _on_mousewheel)
        canvas.bind("<Button-5>", _on_mousewheel)

        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        for idx, item in enumerate(results[:50]):
            if isinstance(item, tuple) and len(item) >= 2:
                album_id, album_title = item[0], item[1]
            else:
                album_id = getattr(item, 'id', str(idx))
                album_title = getattr(item, 'title', str(item))

            item_frame = ttk.Frame(scrollable_frame, relief=tk.RIDGE, borderwidth=1)
            item_frame.pack(fill=tk.X, padx=5, pady=2)

            item_frame.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))

            var = tk.BooleanVar()

            def make_callback(album_id, current_var):
                def on_change(*args):
                    self.update_album_id_entry(album_id, current_var.get())

                return on_change

            var.trace('w', make_callback(album_id, var))
            cb = ttk.Checkbutton(item_frame, variable=var)
            cb.pack(side=tk.LEFT, padx=5, pady=5)

            info_text = f"ID: {album_id}  |  {album_title}"
            info_label = ttk.Label(item_frame, text=info_text, cursor='hand2')
            info_label.pack(side=tk.LEFT, padx=10, pady=5, fill=tk.X, expand=True)

            info_label.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))

            info_label.bind('<Button-1>', lambda e, v=var: v.set(not v.get()))

        scrollable_frame.update_idletasks()
        canvas.configure(scrollregion=canvas.bbox("all"))

        window.focus_set()

    def display_ranking_tab_final(self, scrollable_frame, albums, canvas):
        for widget in scrollable_frame.winfo_children():
            widget.destroy()

        for idx, item in enumerate(albums[:50]):
            if isinstance(item, str):
                info_text = item
                try:
                    parts = item.split("|")
                    if len(parts) >= 1:
                        album_id = parts[0].split(":")[1].strip()
                    else:
                        album_id = str(idx)
                except:
                    album_id = str(idx)
            else:
                if isinstance(item, (list, tuple)) and len(item) >= 2:
                    album_id, album_title = item[0], item[1]
                    if hasattr(album_title, '__call__'):
                        album_title = album_title()
                    elif not isinstance(album_title, str):
                        album_title = str(album_title)
                else:
                    album_id = getattr(item, 'id', str(idx))
                    album_title = getattr(item, 'title', str(item))
                    if hasattr(album_title, '__call__'):
                        album_title = album_title()
                    elif not isinstance(album_title, str):
                        album_title = str(album_title)

                album_id_str = str(album_id) if not isinstance(album_id, str) else album_id
                rank = idx + 1
                info_text = f"ID: {album_id_str} |{rank}| {album_title}"

            item_frame = ttk.Frame(scrollable_frame, relief=tk.RIDGE, borderwidth=1)
            item_frame.pack(fill=tk.X, padx=5, pady=2)

            item_frame.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            item_frame.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))

            var = tk.BooleanVar()

            def make_callback(album_id, current_var):
                def on_change(*args):
                    self.update_album_id_entry(album_id, current_var.get())

                return on_change

            var.trace('w', make_callback(album_id, var))
            cb = ttk.Checkbutton(item_frame, variable=var)
            cb.pack(side=tk.LEFT, padx=5, pady=5)

            info_label = ttk.Label(item_frame, text=info_text, cursor='hand2')
            info_label.pack(side=tk.LEFT, padx=10, pady=5, fill=tk.X, expand=True)

            info_label.bind("<MouseWheel>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-4>", lambda e, c=canvas: self._forward_wheel_event(e, c))
            info_label.bind("<Button-5>", lambda e, c=canvas: self._forward_wheel_event(e, c))

            info_label.bind('<Button-1>', lambda e, v=var: v.set(not v.get()))

        scrollable_frame.update_idletasks()
        canvas.configure(scrollregion=canvas.bbox("all"))

    def update_album_id_entry(self, album_id, is_checked):
        current_ids = self.album_id_entry.get().strip()
        ids_list = [id.strip() for id in current_ids.split(',') if id.strip()] if current_ids else []

        if is_checked:
            if album_id not in ids_list:
                ids_list.append(album_id)
        else:
            if album_id in ids_list:
                ids_list.remove(album_id)

        new_ids = ','.join(ids_list) if ids_list else ""
        self.album_id_entry.delete(0, tk.END)
        self.album_id_entry.insert(0, new_ids)

        self.update_download_path()

        if hasattr(self, '_fetch_timer'):
            self.root.after_cancel(self._fetch_timer)
        self._fetch_timer = self.root.after(800, self.fetch_album_title_auto)

    def show_ranking_dialog(self):
        ranking_window = tk.Toplevel(self.root)
        ranking_window.title("🏆 排行榜")
        ranking_window.geometry("900x700")

        screen_width = ranking_window.winfo_screenwidth()
        screen_height = ranking_window.winfo_screenheight()
        x = (screen_width - 900) // 2
        y = (screen_height - 700) // 2
        ranking_window.geometry(f"+{x}+{y}")

        tab_control = ttk.Notebook(ranking_window)

        day_tab = ttk.Frame(tab_control)
        week_tab = ttk.Frame(tab_control)
        month_tab = ttk.Frame(tab_control)

        tab_control.add(day_tab, text="📅 日排行")
        tab_control.add(week_tab, text="📆 周排行")
        tab_control.add(month_tab, text="📆 月排行")
        tab_control.pack(expand=1, fill=tk.BOTH, padx=10, pady=10)

        self.create_ranking_tab_final(day_tab, 'day')
        self.create_ranking_tab_final(week_tab, 'week')
        self.create_ranking_tab_final(month_tab, 'month')

    def create_ranking_tab_final(self, parent, time_type):
        scroll_frame = ttk.Frame(parent)
        scroll_frame.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)

        canvas = tk.Canvas(scroll_frame, highlightthickness=0)
        scrollbar = ttk.Scrollbar(scroll_frame, orient="vertical", command=canvas.yview)
        scrollable_frame = ttk.Frame(canvas)

        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )

        canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)

        def _on_mousewheel(event):
            if event.delta:
                canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
            else:
                if event.num == 4:
                    canvas.yview_scroll(-1, "units")
                elif event.num == 5:
                    canvas.yview_scroll(1, "units")
            return "break"

        canvas.bind("<MouseWheel>", _on_mousewheel)
        canvas.bind("<Button-4>", _on_mousewheel)
        canvas.bind("<Button-5>", _on_mousewheel)

        canvas.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        current_time = time.time()
        cache_valid = (
                self.ranking_cache[time_type] is not None
        )

        if cache_valid:
            loading_label = ttk.Label(scrollable_frame, text="正在加载缓存数据...", font=('Arial', 14))
            loading_label.pack(pady=50)
            self.root.after(100, lambda: self.display_ranking_tab_final(scrollable_frame, self.ranking_cache[time_type],
                                                                        canvas))
        else:
            loading_label = ttk.Label(scrollable_frame, text="正在加载排行榜...", font=('Arial', 14))
            loading_label.pack(pady=50)

            def load_thread():
                try:
                    import jmcomic
                    from jmcomic import JmOption

                    option = JmOption.default()
                    client = option.new_jm_client()

                    if time_type == 'day':
                        ranking_page = client.day_ranking(page=1)
                    elif time_type == 'week':
                        ranking_page = client.week_ranking(page=1)
                    else:
                        ranking_page = client.month_ranking(page=1)

                    albums = list(ranking_page)

                    self.ranking_cache[time_type] = albums
                    self.ranking_cache_time[time_type] = current_time

                    self.root.after(0, lambda: self.display_ranking_tab_final(scrollable_frame, albums, canvas))

                except Exception as e:
                    self.root.after(0, lambda: loading_label.config(
                        text=f"加载失败: {e}", foreground='red'
                    ))

            thread = threading.Thread(target=load_thread, daemon=True)
            thread.start()

    def _is_same_day(self, timestamp1, timestamp2):
        date1 = datetime.datetime.fromtimestamp(timestamp1).date()
        date2 = datetime.datetime.fromtimestamp(timestamp2).date()
        return date1 == date2

    def save_ranking_cache(self):
        pass

    def load_ranking_cache(self):
        pass

    def clear_ranking_cache(self):
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

    def on_closing(self):
        self.root.destroy()

def main():
    root = ttk.Window(themename="flatly")
    app = JMComicGUI(root)

    try:
        root.iconbitmap('icon.ico')
    except:
        pass

    root.place_window_center()

    root.mainloop()

if __name__ == "__main__":
    main()