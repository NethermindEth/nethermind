# Building a Debian package

Building a Debian (.deb) package requires Docker. Run the following from the repo root:

```bash
scripts/build/deb/build.sh [path/to/output/dir]
```

The `path/to/output/dir` is optional. If not specified, the generated `nethermind.deb` package is copied to the repo root.
