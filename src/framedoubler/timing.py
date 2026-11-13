"""Optional exclusive wall-clock timings, including waits at process boundaries."""

import time
from collections import defaultdict
from contextlib import contextmanager
from dataclasses import dataclass


@dataclass(frozen=True)
class TimingReport:
    total: float
    seconds: dict[str, float]


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
        self._stack = []

    @contextmanager
    def measure(self, name):
        if not self.enabled:
            yield
            return
        entry = [self.clock(), 0.0]
        self._stack.append(entry)
        try:
            yield
        finally:
            elapsed = self.clock() - entry[0]
            self._stack.pop()
            self.seconds[name] += elapsed - entry[1]
            if self._stack:
                self._stack[-1][1] += elapsed

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
        seconds = dict(self.seconds)
        seconds["other"] = max(0.0, total - sum(seconds.values()))
        return TimingReport(total, seconds)
