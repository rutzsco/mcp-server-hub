from fastapi import FastAPI
from pydantic import BaseModel
import os
import time


app = FastAPI(title="Hello Work API", version="0.1.0")


class StatusResponse(BaseModel):
    status: str
    uptime_seconds: float
    pid: int


start_time = time.time()


@app.get("/")
def hello_work():
    return {"message": "Hello, work"}


@app.get("/status", response_model=StatusResponse)
def status():
    uptime = time.time() - start_time
    return StatusResponse(status="ok", uptime_seconds=uptime, pid=os.getpid())
