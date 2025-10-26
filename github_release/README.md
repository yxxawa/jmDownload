# JMComic Downloader

JMComic Downloader is a comic download tool specifically designed for JMComic websites, built with Python and Tkinter, providing a clean and user-friendly graphical interface.

## Features

- **Modern UI**: Built with ttkbootstrap, featuring a modern Windows 11 style interface
- **Search Function**: Quickly search for comics by keywords
- **Rankings**: View daily, weekly, and monthly popular rankings
- **Batch Download**: Support downloading multiple comic or chapter IDs simultaneously
- **Cover Preview**: Preview and zoom in on comic covers
- **Multiple Themes**: Choose from various built-in interface themes
- **Advanced Settings**: Support image format conversion, concurrency control, and other advanced options
- **Auto Path Management**: Automatically create and manage download directory structures

## Preview

![Main Interface](icon.png)

## Installation

### Option 1: Using Pre-built EXE (Recommended)

1. Download the latest version of `JMComic下载器.exe` from the [Releases](https://github.com/your-username/jmcomic-downloader/releases) page
2. Run it directly without installing Python environment

### Option 2: Running from Source

1. Make sure Python 3.7+ is installed
2. Clone or download the project source code
3. Install dependencies:
   ```bash
   pip install ttkbootstrap pillow jmcomic
   ```
4. Run the program:
   ```bash
   python src/jmcomic_gui.py
   ```

## Usage

1. **Enter ID**: Input the comic or chapter ID in the ID input box
2. **Set Path**: Confirm the download save path (default is the JMDownLoad folder in the program directory)
3. **Advanced Options**: Configure image format, concurrency, and other options as needed
4. **Start Download**: Click the "Start Download" button

### Supported ID Formats

- Comic ID: `422866`
- Chapter ID: `p456`
- Multiple IDs: `422866,422867` or `422866 422867`

## Function Guide

### Search Function
Enter keywords in the search box at the top, click the "Search" button or press Enter to view search results, and select comics to add to the download list.

### Rankings
Click the "View Rankings" button at the bottom to view daily, weekly, and monthly popular rankings, and select comics to add to the download list.

### Cover Preview
Enter the comic ID and click the "Show Cover" button to view the cover. Click the cover image to zoom in.

### Interface Themes
Click the "Interface Theme" button at the bottom to choose your preferred theme.

## Configuration

The program generates the following configuration files in the running directory:

- `style_cache.json`: Saves interface theme settings
- `JMDownLoad/`: Default download directory

## Development

### Project Structure

```
JMComic/
├── src/
│   ├── jmcomic_gui.py      # Main program file
│   ├── build_exe.py        # Packaging script
│   ├── run_gui.py          # Startup script
│   ├── option_example.yml  # Configuration example file
│   └── 启动程序.bat         # Windows startup script
├── README.md               # Documentation
├── LICENSE                 # License file
├── .gitignore              # Git ignore file
└── icon.png                # Program icon
```

### Building EXE

```bash
python src/build_exe.py
```

The generated exe file will be located at `dist/JMComic下载器.exe`

## Dependencies

- [ttkbootstrap](https://github.com/israel-dryer/ttkbootstrap) - Modern Tkinter theme
- [Pillow](https://python-pillow.org/) - Image processing library
- [jmcomic](https://github.com/hect0x7/JMComic-Crawler-Python) - JMComic crawler library

## Disclaimer

This project is for learning and communication purposes only. The downloaded comic content is copyrighted by their respective owners. Please respect copyrights and use the content reasonably.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.