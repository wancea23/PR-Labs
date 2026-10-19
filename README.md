# PR

Laboratory works for the Network Programming course (PR) at the Technical University of Moldova.

## Labs

| Lab | Topic | Status | Report |
|-----|-------|--------|--------|
| Lab1 | Concurrency in practice | Done | [Lab1/Lab1_IonMorosanu_FAF-242.pdf](Lab1/Lab1_IonMorosanu_FAF-242.pdf) |

## Lab1: Concurrency in practice

One C# program counts the negative, zero and positive numbers among 50 000 000 integers with different numbers of threads and different ways of dividing the work. The task and its configuration are selected on the command line.

| Command | Task |
|---------|------|
| `gen <file>` | generates the 50 000 000 random numbers |
| `split <file> <dir>` | splits the file into 8 files for Task 2 |
| `1a <file> <threads>` | every thread counts its own part of the file |
| `1b <file>` | shared counters, a lock, per-thread counters |
| `1c <file> <workers>` | one reader, workers take batches from a bounded queue |
| `2 <dir> <threads>` | every thread reads whole files from a USB drive |

### Running

Requires the .NET 10 SDK. From the `Lab1` folder:

```
dotnet run -c Release -- gen numbers.txt
dotnet run -c Release -- split numbers.txt parts
dotnet run -c Release -- 1a numbers.txt 8
dotnet run -c Release -- 1b numbers.txt
dotnet run -c Release -- 1c numbers.txt 8
dotnet run -c Release -- 2 E:\prlab1 4
```

`run_task1.ps1` runs every configuration of Task 1 five times. `run_task2.ps1 -Drive E` copies the 8 files to the USB drive and waits for it to be unplugged and plugged back in before every run. The measurements used in the report are in `Lab1/results`.
