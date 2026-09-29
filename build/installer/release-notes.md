## Install

Download **JaneSetup.exe** below and run it. It needs no administrator account.

This build is not code-signed, so Windows SmartScreen will say it does not recognise the
publisher. Choose **More info**, then **Run anyway**.

The first time Jane runs it downloads its speech model and offers to download the language model
that tidies up what you say. Together they need about 6 GB of disk. After that, nothing you
dictate leaves your machine.

## What this build cannot do

It cannot type into a window that is running as administrator. That needs a code-signed build,
which this is not. Everything else works.

## Check the download

```
SHA-256  {SHA256}
```

In PowerShell: `Get-FileHash .\JaneSetup.exe -Algorithm SHA256`
