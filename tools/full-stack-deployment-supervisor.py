#!/usr/bin/env python3
"""Run a deployment smoke command in, and clean up, one process group.

GNU timeout stops supervising when the command it started exits.  A Docker
wrapper can exit at that point while a child is still running in the wrapper's
process group.  This supervisor keeps the process-group identity and performs
one final group sweep after the smoke leader exits, including on normal
success.
"""

from __future__ import annotations

import argparse
import os
import signal
import subprocess
import sys
import time
from dataclasses import dataclass
from typing import Iterable


@dataclass(frozen=True)
class ProcessInfo:
    pid: int
    state: str
    pgid: int
    session: int
    start_time: int


def read_process_info(pid: int) -> ProcessInfo | None:
    """Read the Linux process identity fields needed for safe signalling."""
    try:
        with open(f"/proc/{pid}/stat", encoding="ascii") as stream:
            text = stream.read()
    except OSError:
        return None

    # comm can contain spaces and parentheses.  The final ')' is the end of
    # comm because the remaining fields are all whitespace separated.
    closing_paren = text.rfind(")")
    if closing_paren < 0:
        return None
    fields = text[closing_paren + 2 :].split()
    # After comm, fields[0] is state (field 3), fields[2] is pgrp (field 5),
    # fields[3] is session (field 6), and fields[19] is starttime (field 22).
    if len(fields) <= 19:
        return None
    try:
        return ProcessInfo(
            pid=pid,
            state=fields[0],
            pgid=int(fields[2]),
            session=int(fields[3]),
            start_time=int(fields[19]),
        )
    except ValueError:
        return None


def live(info: ProcessInfo) -> bool:
    # A zombie has finished execution.  It cannot be made more dead with
    # SIGKILL, and its parent/PID 1 is responsible for reaping the entry.
    return info.state != "Z"


def parse_duration(value: str) -> float:
    units = {"s": 1.0, "m": 60.0, "h": 3600.0}
    if not value or value[-1] not in units:
        raise argparse.ArgumentTypeError("duration must end in s, m, or h")
    try:
        seconds = float(value[:-1]) * units[value[-1]]
    except ValueError as error:
        raise argparse.ArgumentTypeError("duration must be numeric") from error
    if seconds <= 0:
        raise argparse.ArgumentTypeError("duration must be positive")
    return seconds


class GroupSupervisor:
    def __init__(self, process: subprocess.Popen[bytes], grace: float) -> None:
        self.process = process
        self.grace = grace
        self.leader_pid = process.pid
        self.leader_start_time: int | None = None
        self.pgid: int | None = None
        self.session: int | None = None
        self._capture_identity()

    def _capture_identity(self) -> None:
        # start_new_session=True makes the bash process both a session and
        # process-group leader.  Capture its /proc start time as well as the
        # numeric IDs before a fast command can exit and its PID be reused.
        for _ in range(100):
            # Check the Popen handle first.  This prevents a reused numeric PID
            # from being accepted as the leader after the original exits.
            if self.process.poll() is not None:
                break
            info = read_process_info(self.leader_pid)
            if info is not None:
                self.leader_start_time = info.start_time
                self.pgid = info.pgid
                self.session = info.session
                return
            time.sleep(0.001)

        # A process that exits before /proc can be read still has the IDs
        # promised by start_new_session.  Do not claim leader identity without
        # start_time, but retain the IDs so an identity-checked member sweep can
        # clean a descendant that was left behind by a very fast command.
        self.pgid = self.leader_pid
        self.session = self.leader_pid

    def leader_is_original(self) -> bool:
        if self.pgid is None or self.session is None or self.leader_start_time is None:
            return False
        info = read_process_info(self.leader_pid)
        return bool(
            info
            and info.pid == self.leader_pid
            and info.start_time == self.leader_start_time
            and info.pgid == self.pgid
            and info.session == self.session
        )

    def members(self) -> list[ProcessInfo]:
        if self.pgid is None or self.session is None:
            return []
        result: list[ProcessInfo] = []
        try:
            entries: Iterable[os.DirEntry[str]] = os.scandir("/proc")
        except OSError:
            return result
        with entries:
            for entry in entries:
                if not entry.name.isdigit():
                    continue
                info = read_process_info(int(entry.name))
                if info and info.pgid == self.pgid and info.session == self.session:
                    result.append(info)
        return result

    def live_members(self) -> list[ProcessInfo]:
        return [info for info in self.members() if live(info)]

    @staticmethod
    def _same_process(current: ProcessInfo | None, expected: ProcessInfo) -> bool:
        return bool(
            current
            and current.pid == expected.pid
            and current.start_time == expected.start_time
            and current.pgid == expected.pgid
            and current.session == expected.session
        )

    def _signal_members(self, members: list[ProcessInfo], signum: int) -> None:
        # Once the leader has exited, use per-process identity checks rather
        # than killpg: the numeric PGID is the old leader PID and could be
        # reused.  Re-read /proc immediately before every signal and require
        # the member's captured start time, session, and process group.
        for expected in members:
            if not live(expected):
                continue
            current = read_process_info(expected.pid)
            if not self._same_process(current, expected):
                continue
            try:
                os.kill(expected.pid, signum)
            except ProcessLookupError:
                pass

    def signal_group(self, signum: int) -> None:
        # While the original leader is alive, validating its identity makes a
        # group signal safe.  This is the only broad killpg operation.  If the
        # leader won the race to exit, fall back to identity-checked members.
        if self.pgid is None:
            return
        if self.leader_is_original():
            try:
                os.killpg(self.pgid, signum)
                return
            except ProcessLookupError:
                pass
        self._signal_members(self.live_members(), signum)

    def reap_remaining(self, deadline: float | None = None) -> None:
        """TERM and then KILL every live member left in this exact session."""
        remaining = self.live_members()
        if not remaining:
            return
        self._signal_members(remaining, signal.SIGTERM)
        end = time.monotonic() + self.grace if deadline is None else deadline
        while time.monotonic() < end:
            if not self.live_members():
                return
            time.sleep(min(0.05, max(0.001, end - time.monotonic())))
        remaining = self.live_members()
        if remaining:
            self._signal_members(remaining, signal.SIGKILL)
            # Give /proc a bounded opportunity to reflect termination.  A
            # zombie is deliberately not considered live here.
            end = time.monotonic() + min(1.0, self.grace)
            while time.monotonic() < end and self.live_members():
                time.sleep(0.01)


def child_status(returncode: int | None) -> int:
    if returncode is None:
        return 1
    return returncode if returncode >= 0 else 128 + (-returncode)


def run(deadline: float, grace: float, command: list[str]) -> int:
    interrupted: int | None = None

    def request_signal(signum: int, _frame: object) -> None:
        nonlocal interrupted
        interrupted = signum

    for signum in (signal.SIGINT, signal.SIGTERM):
        signal.signal(signum, request_signal)

    process = subprocess.Popen(command, start_new_session=True)
    deadline_at = time.monotonic() + deadline
    supervisor = GroupSupervisor(process, grace)
    term_deadline: float | None = None
    timed_out = False
    external_signal: int | None = None

    while process.poll() is None:
        now = time.monotonic()
        if interrupted is not None:
            external_signal = interrupted
            interrupted = None
            supervisor.signal_group(external_signal)
            term_deadline = now + grace
        elif term_deadline is None and now >= deadline_at:
            timed_out = True
            supervisor.signal_group(signal.SIGTERM)
            term_deadline = now + grace
        if term_deadline is not None and now >= term_deadline:
            supervisor._signal_members(supervisor.live_members(), signal.SIGKILL)
            break
        time.sleep(0.05)

    returncode = process.wait()
    if term_deadline is not None:
        # The TERM grace period starts when the group was signalled.  Always
        # sweep after the leader exits, even if the leader returned normally.
        supervisor.reap_remaining(term_deadline)
    else:
        # Normal success/failure still gets a bounded escaped-child cleanup.
        supervisor.reap_remaining()

    if timed_out:
        return 124
    if external_signal is not None:
        return 128 + external_signal
    return child_status(returncode)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--deadline", type=parse_duration, default=parse_duration("40m"))
    parser.add_argument("--grace", type=parse_duration, default=parse_duration("60s"))
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if not args.command or args.command[0] != "--":
        parser.error("command must follow --")
    command = args.command[1:]
    if not command:
        parser.error("missing command")
    return run(args.deadline, args.grace, command)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
