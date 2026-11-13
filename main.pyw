"""Double-click entry point; launch.ps1 also discovers pythonw automatically."""
from pathlib import Path
import sys
import traceback

if __name__ == "__main__":
    try:
        from jiangshen_assistant.app import main
        main()
    except Exception:
        error = traceback.format_exc()
        target = Path(__file__).resolve().parent / "data" / "startup-error.log"
        try:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(error, encoding="utf-8")
        except OSError:
            pass
        if sys.stderr:
            sys.stderr.write(error)
        import ctypes
        ctypes.windll.user32.MessageBoxW(0, f"启动失败，请查看：\n{target}\n\n{error[-1200:]}", "将神助手", 0x10)
