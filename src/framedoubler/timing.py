"""Optional exclusive wall-clock timings, including waits at process boundaries."""

import time
import threading
from collections import defaultdict
from contextlib import contextmanager
from dataclasses import dataclass, field


@dataclass(frozen=True)
class TimingReport:
    total: float
    seconds: dict[str, float]
    worker_seconds: dict[str, float] = field(default_factory=dict)


class _TimedContext:
    def __init__(self, profiler, name, manager):
        self.profiler, self.name, self.manager = profiler, name, manager

    def __enter__(self):
        return self.profiler.call(self.name, self.manager.__enter__)

    def __exit__(self, *args):
        return self.profiler.call(self.name, self.manager.__exit__, *args)


class Profiler:
    def __init__(self, enabled=False, clock=time.perf_counter):
        self.enabled, self.clock = enabled, clock
        self.started = clock() if enabled else 0.0
        self.seconds = defaultdict(float)
        self.worker_seconds = defaultdict(float)
        self._main_thread = threading.get_ident()
        self._local = threading.local()
        self._lock = threading.Lock()

    @contextmanager
    def measure(self, name):
        if not self.enabled:
            yield
            return
        entry = [self.clock(), 0.0]
        if not hasattr(self._local, "stack"):
            self._local.stack = []
        stack = self._local.stack
        stack.append(entry)
        try:
            yield
        finally:
            elapsed = self.clock() - entry[0]
            stack.pop()
            values = self.seconds if threading.get_ident() == self._main_thread else self.worker_seconds
            with self._lock:
                values[name] += elapsed - entry[1]
            if stack:
                stack[-1][1] += elapsed

    def call(self, name, function, *args, **kwargs):
        with self.measure(name):
            return function(*args, **kwargs)

    def iterator(self, name, source):
        iterator = iter(source)
        try:
            while True:
                with self.measure(name):
                    try:
                        item = next(iterator)
                    except StopIteration:
                        return
                yield item
        finally:
            if hasattr(iterator, "close"):
                iterator.close()

    def context(self, name, manager):
        return _TimedContext(self, name, manager)

    def snapshot(self):
        if not self.enabled:
            return None
        total = self.clock() - self.started
        with self._lock:
            seconds = dict(self.seconds)
            workers = dict(self.worker_seconds)
        seconds["other"] = max(0.0, total - sum(seconds.values()))
        return TimingReport(total, seconds, workers)
