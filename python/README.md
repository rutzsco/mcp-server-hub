# Hello Work FastAPI (uv-managed)

This is a minimal FastAPI service living under the `python/` folder. It uses `uv` strictly for package management. The server process is `uvicorn`.

## Endpoints
- `GET /` -> `{ "message": "Hello, work" }`
- `GET /status` -> `{ status, uptime_seconds, pid }`

## Local dev

Install `uv` if you don't have it:

```powershell
# Windows PowerShell
pip install uv
```

Create a venv and install deps:

```powershell
uv venv
uv sync
```

Run the dev server (hot reload) using uvicorn directly (uv is only for deps):

```powershell
# Windows PowerShell
.venv\Scripts\uvicorn.exe app.main:app --reload --host 127.0.0.1 --port 8000
```

## Docker

Build and run:

```powershell
# From the python/ folder
docker build -t hello-work-api .
docker run --rm -p 8000:8000 hello-work-api
```

## Notes
- `uv` is used only for dependency/venv management. The container runs `uvicorn` directly.
- Requires Python 3.11+ locally; the Dockerfile uses Python 3.12.