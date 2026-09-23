// A native entry point for the Linux release. Python is already an installation requirement.
package main

import (
 "fmt"
 "os"
 "os/exec"
 "path/filepath"
)

func main() {
 executable, err := os.Executable()
 if err != nil { fmt.Fprintln(os.Stderr, err); os.Exit(1) }
 script := filepath.Join(filepath.Dir(executable), "deploy", "uninstall.py")
 if _, err = os.Stat(script); err != nil { fmt.Fprintln(os.Stderr, "Missing deploy/uninstall.py; restore the complete package."); os.Exit(1) }
 command := exec.Command("python3", append([]string{script}, os.Args[1:]...)...)
 command.Stdin, command.Stdout, command.Stderr = os.Stdin, os.Stdout, os.Stderr
 if err = command.Run(); err != nil { if e,ok:=err.(*exec.ExitError);ok{os.Exit(e.ExitCode())};fmt.Fprintln(os.Stderr,err);os.Exit(1) }
}
