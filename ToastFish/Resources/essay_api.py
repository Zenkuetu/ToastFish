#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ToastFish Essay API Server
微型 HTTP 服务器，供仪表盘 HTML 调用以删除 AI 短文记录。
仪表盘打开时自动启动（generate_dashboard.py 负责），监听 localhost:8765。
"""

import json
import sqlite3
import os
import sys
import subprocess
from http.server import HTTPServer, BaseHTTPRequestHandler

_SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
DB_PATH = os.path.join(_SCRIPT_DIR, "..", "ToastFish", "Resources", "inami.db")
DB_PATH = os.path.normpath(DB_PATH)
GEN_SCRIPT = os.path.join(_SCRIPT_DIR, "generate_dashboard.py")
PORT = 8765


class EssayAPI(BaseHTTPRequestHandler):

    def do_OPTIONS(self):
        """CORS 预检，文件协议 fetch 必须。"""
        self.send_response(204)
        self._cors()
        self.end_headers()

    def do_POST(self):
        if self.path == '/delete-essay':
            try:
                length = int(self.headers.get('Content-Length', 0))
                body = json.loads(self.rfile.read(length))
                essay_id = body.get('id')
                if essay_id is None:
                    self._json({'ok': False, 'error': '缺少 id'}, 400)
                    return

                conn = sqlite3.connect(DB_PATH)
                cursor = conn.execute(
                    'DELETE FROM EssayLog WHERE id = ?', (int(essay_id),))
                deleted = cursor.rowcount
                conn.commit()
                conn.close()

                if deleted > 0:
                    self._json({'ok': True, 'deleted': deleted})
                    # 删除成功后重新生成仪表盘，下次刷新即生效
                    _regenerate_dashboard()
                else:
                    self._json({'ok': False, 'error': f'id={essay_id} 不存在'}, 404)
            except Exception as e:
                self._json({'ok': False, 'error': str(e)}, 500)
        else:
            self._json({'ok': False, 'error': '未知路径'}, 404)

    def _cors(self):
        self.send_header('Access-Control-Allow-Origin', '*')
        self.send_header('Access-Control-Allow-Methods', 'POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type')

    def _json(self, data, status=200):
        self.send_response(status)
        self._cors()
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.end_headers()
        self.wfile.write(json.dumps(data, ensure_ascii=False).encode('utf-8'))

    def log_message(self, format, *args):
        print(f"[API] {args[0]}")


def _regenerate_dashboard():
    """后台重新生成仪表盘 HTML。"""
    try:
        if sys.platform == 'win32':
            subprocess.Popen(
                [sys.executable, GEN_SCRIPT],
                creationflags=subprocess.CREATE_NO_WINDOW,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
            )
        else:
            subprocess.Popen(
                [sys.executable, GEN_SCRIPT],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
            )
    except Exception as e:
        print(f"[API] 仪表盘重新生成失败: {e}")


def main():
    print(f"ToastFish Essay API Server")
    print(f"  数据库: {DB_PATH}")
    print(f"  http://localhost:{PORT}/delete-essay")
    print(f"  按 Ctrl+C 停止")
    server = HTTPServer(('127.0.0.1', PORT), EssayAPI)
    try:
        server.serve_forever()
    except OSError as e:
        if getattr(e, 'winerror', None) == 10048:  # 端口已被占用——另一个实例已在运行
            print(f"端口 {PORT} 已被占用，API 服务器可能已在运行中。")
        else:
            raise
    except KeyboardInterrupt:
        print("\n已停止。")


if __name__ == '__main__':
    main()
