# Contributing

Jane is a personal project, published so the work can be read. It is not looking for feature
contributions, and I may be slow to respond to anything.

That said — issues are open, and these are genuinely useful:

- **Bug reports.** Especially anything about text landing in the wrong window, a hotkey that stops
  responding, or an application Jane types into badly. Include your Windows version and the output
  of `dotnet run --project src/Jane.Bench -- doctor`.
- **Security reports.** Please read [SECURITY.md](SECURITY.md) first and use private reporting.
- **Corrections.** If something in the README, [NOTICE.md](NOTICE.md) or the design notes is wrong,
  say so.

Please do not send large refactors or new features unprompted. Open an issue first — I would rather
tell you "no thanks" before you spend an evening on it than after.

## If you do send a pull request

The house rules the existing code follows:

- **Tests first.** Red, green, refactor. Every behavioural change has a test that failed before the
  change and passes after it. Bug fixes start with a regression test that reproduces the bug.
- **`Jane.Core` stays free of Win32.** That constraint is what makes the pipeline testable with
  fakes instead of by holding a key down and hoping. Platform code belongs in `Jane.Windows`.
- **Comments explain why, not what.** The existing comments say what was rejected and what the
  tradeoff was. Match that; do not narrate the code.
- **`dotnet format --verify-no-changes` passes**, and so does `dotnet test`.

Run everything before pushing:

```powershell
dotnet format --verify-no-changes
dotnet test
```

Some tests touch real audio devices, a real Win32 window and UI Automation, so they need an
interactive Windows session — they will not pass over SSH or in a container.

**Quit Jane first.** Several tests install their own keyboard hook and open the capture device, and
a running copy of Jane holds both. `Jane.Windows.Tests` can block partway through the assembly when
it has to share them; running it class by class is the workaround if you hit that.
