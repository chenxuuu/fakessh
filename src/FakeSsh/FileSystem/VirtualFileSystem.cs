namespace FakeSsh.FileSystem;

public enum VfsNodeType
{
    File,
    Directory
}

public class VfsNode
{
    public string Path { get; set; }
    public VfsNodeType Type { get; set; }
    public string Content { get; set; } = "";
    public string Permissions { get; set; } = "rwxr-xr-x";
    public string Owner { get; set; } = "root";
    public string Group { get; set; } = "root";
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public bool IsUserCreated { get; set; }

    public VfsNode(string path, VfsNodeType type)
    {
        Path = path;
        Type = type;
    }
}

public class VirtualFileSystem
{
    private readonly Dictionary<string, VfsNode> _nodes = new();
    private readonly List<string> _changeLog = new();
    private readonly object _lock = new();

    public VirtualFileSystem(string username)
    {
        InitializeDefaultTree(username);
    }

    private void InitializeDefaultTree(string username)
    {
        var systemDirs = new[]
        {
            "/", "/bin", "/boot", "/dev", "/etc", "/etc/apt", "/etc/ssh",
            "/etc/default", "/etc/network", "/etc/systemd", "/etc/cron.d",
            "/home", "/lib", "/lib/x86_64-linux-gnu", "/lib64",
            "/media", "/mnt", "/opt",
            "/proc", "/root", "/run", "/sbin", "/srv", "/sys", "/tmp",
            "/usr", "/usr/bin", "/usr/lib", "/usr/local", "/usr/local/bin",
            "/usr/share", "/usr/sbin",
            "/var", "/var/log", "/var/lib", "/var/lib/apt",
            "/var/cache", "/var/cache/apt", "/var/tmp", "/var/run",
            "/var/spool", "/var/mail"
        };

        foreach (var dir in systemDirs)
        {
            AddNode(dir, VfsNodeType.Directory, permissions: "rwxr-xr-x");
        }

        // Home dir for non-root users
        if (username != "root")
        {
            AddNode($"/home/{username}", VfsNodeType.Directory, permissions: "rwx------", owner: username);
        }

        // Key system files
        AddFile("/etc/hostname", "debian-srv", "rw-r--r--");
        AddFile("/etc/hosts", "127.0.0.1\tlocalhost\n127.0.1.1\tdebian-srv\n\n::1\tlocalhost ip6-localhost ip6-loopback\nff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n", "rw-r--r--");
        AddFile("/etc/os-release",
            "PRETTY_NAME=\"Debian GNU/Linux 12 (bookworm)\"\nNAME=\"Debian GNU/Linux\"\nVERSION_ID=\"12\"\nVERSION=\"12 (bookworm)\"\nVERSION_CODENAME=bookworm\nID=debian\nHOME_URL=\"https://www.debian.org/\"\nSUPPORT_URL=\"https://www.debian.org/support\"\nBUG_REPORT_URL=\"https://bugs.debian.org/\"\n",
            "rw-r--r--");
        AddFile("/etc/debian_version", "12.5\n", "rw-r--r--");
        AddFile("/etc/passwd",
            "root:x:0:0:root:/root:/bin/bash\ndaemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\nbin:x:2:2:bin:/bin:/usr/sbin/nologin\nsys:x:3:3:sys:/dev:/usr/sbin/nologin\nsync:x:4:65534:sync:/bin:/bin/sync\ngames:x:5:60:games:/usr/games:/usr/sbin/nologin\nman:x:6:12:man:/var/cache/man:/usr/sbin/nologin\nlp:x:7:7:lp:/var/spool/lpd:/usr/sbin/nologin\nmail:x:8:8:mail:/var/mail:/usr/sbin/nologin\nnews:x:9:9:news:/var/spool/news:/usr/sbin/nologin\nuucp:x:10:10:uucp:/var/spool/uucp:/usr/sbin/nologin\nproxy:x:13:13:proxy:/bin:/usr/sbin/nologin\nwww-data:x:33:33:www-data:/var/www:/usr/sbin/nologin\nbackup:x:34:34:backup:/var/backups:/usr/sbin/nologin\nlist:x:38:38:Mailing List Manager:/var/list:/usr/sbin/nologin\nirc:x:39:39:ircd:/run/ircd:/usr/sbin/nologin\n_apt:x:42:65534::/nonexistent:/usr/sbin/nologin\nnobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin\nsystemd-network:x:998:998:systemd Network Management:/:/usr/sbin/nologin\nsshd:x:100:65534::/run/sshd:/usr/sbin/nologin\n",
            "rw-r--r--");
        AddFile("/etc/shadow", "root:$6$rounds=656000$randomsalt$hashedpassword:19750:0:99999:7:::\n", "rw-------");
        AddFile("/etc/group", "root:x:0:\ndaemon:x:1:\nbin:x:2:\nsys:x:3:\nadm:x:4:\ntty:x:5:\ndisk:x:6:\nlp:x:7:\nmail:x:8:\nnews:x:9:\nuucp:x:10:\nman:x:12:\nproxy:x:13:\nkmem:x:15:\ndialout:x:20:\nfax:x:21:\nvoice:x:22:\ncdrom:x:24:\nfloppy:x:25:\ntape:x:26:\nsudo:x:27:\naudio:x:29:\ndip:x:30:\nwww-data:x:33:\nbackup:x:34:\noperator:x:37:\nlist:x:38:\nirc:x:39:\nsrc:x:40:\ngnats:x:41:\nshadow:x:42:\nutmp:x:43:\nvideo:x:44:\nsasl:x:45:\nplugin:x:46:\nstaff:x:50:\ngames:x:60:\nusers:x:100:\nnogroup:x:65534:\nsshd:x:101:\nsystemd-network:x:998:\n", "rw-r--r--");
        AddFile("/etc/resolv.conf", "nameserver 8.8.8.8\nnameserver 8.8.4.4\n", "rw-r--r--");
        AddFile("/etc/fstab", "# /etc/fstab: static file system information.\n#\n# <file system> <mount point> <type> <options> <dump> <pass>\nUUID=a1b2c3d4-e5f6-7890-abcd-ef1234567890 / ext4 errors=remount-ro 0 1\n", "rw-r--r--");
        AddFile("/etc/ssh/sshd_config", "# OpenSSH server configuration\nPort 22\nPermitRootLogin yes\nPasswordAuthentication yes\nChallengeResponseAuthentication no\nUsePAM yes\nX11Forwarding yes\nPrintMotd no\nAcceptEnv LANG LC_*\nSubsystem sftp /usr/lib/openssh/sftp-server\n", "rw-r--r--");
        AddFile("/etc/network/interfaces", "# This file describes the network interfaces available on your system\n# and how to activate them.\n\nsource /etc/network/interfaces.d/*\n\n# The loopback network interface\nauto lo\niface lo inet loopback\n\n# The primary network interface\nallow-hotplug eth0\niface eth0 inet dhcp\n", "rw-r--r--");

        // Root home files
        AddFile("/root/.bashrc", "# ~/.bashrc: executed by bash(1) for non-login shells.\nexport PS1='\\u@\\h:\\w\\$ '\numask 022\nexport LS_OPTIONS='--color=auto'\neval \"$(dircolors)\"\nalias ls='ls $LS_OPTIONS'\nalias ll='ls $LS_OPTIONS -l'\nalias l='ls $LS_OPTIONS -lA'\nalias rm='rm -i'\nalias cp='cp -i'\nalias mv='mv -i'\n", "rw-r--r--");
        AddFile("/root/.profile", "# ~/.profile: executed by Bourne-compatible login shells.\nif [ \"$BASH\" ]; then\n  if [ -f ~/.bashrc ]; then\n    . ~/.bashrc\n  fi\nfi\nmesg n 2>/dev/null || true\n", "rw-r--r--");

        // Proc pseudo-files (read-only info)
        AddFile("/proc/version", "Linux version 6.1.0-18-amd64 (debian-kernel@lists.debian.org) (gcc-12 (Debian 12.2.0-14) 12.2.0, GNU ld (GNU Binutils for Debian) 2.40) #1 SMP PREEMPT_DYNAMIC Debian 6.1.76-1 (2024-02-01)\n", "r--r--r--");
        AddFile("/proc/cpuinfo", "processor\t: 0\nvendor_id\t: AuthenticAMD\ncpu family\t: 25\nmodel\t\t: 17\nmodel name\t: AMD EPYC 9754 128-Core Processor\nstepping\t: 1\nmicrocode\t: 0xa101144\ncpu MHz\t\t: 3100.000\ncache size\t: 65536 KB\nphysical id\t: 0\nsiblings\t: 256\ncore id\t\t: 0\ncpu cores\t: 128\nbogomips\t: 6200.00\nflags\t\t: fpu vme de pse tsc msr pae mce cx8 apic sep mtrr pge mca cmov pat pse36 clflush mmx fxsr sse sse2 ht syscall nx mmxext fxsr_opt pdpe1gb rdtscp lm constant_tsc rep_good nopl nonstop_tsc cpuid extd_apicid aperfmperf rapl pni pclmulqdq monitor ssse3 fma cx16 pcid sse4_1 sse4_2 x2apic movbe popcnt aes xsave avx f16c rdrand lahf_lm cmp_legacy svm extapic cr8_legacy abm sse4a misalignsse 3dnowprefetch osvw ibs skinit wdt tce topoext perfctr_core perfctr_nb bpext perfctr_llc mwaitx cpb cat_l3 cdp_l3 hw_pstate ssbd mba perfmon_v2 ibrs ibpb stibp ibrs_enhanced vmmcall fsgsbase bmi1 avx2 smep bmi2 erms invpcid cqm rdt_a avx512f avx512dq rdseed adx smap avx512ifma clflushopt clwb avx512cd sha_ni avx512bw avx512vl xsaveopt xsavec xgetbv1 xsaves cqm_llc cqm_occup_llc cqm_mbm_total cqm_mbm_local avx512_bf16 clzero irperf xsaveerptr rdpru wbnoinvd cppc arat npt lbrv svm_lock nrip_save tsc_scale vmcb_clean flushbyasid decodeassists pausefilter pfthreshold avx512vbmi umip pku ospke avx512_vbmi2 gfni vaes vpclmulqdq avx512_vnni avx512_bitalg avx512_vpopcntdq rdpid overflow_recov succor smca fsrm flush_l1d\n\n", "r--r--r--");
        AddFile("/proc/meminfo", "MemTotal:        10737418240 kB\nMemFree:         2306867200 kB\nMemAvailable:    8589934592 kB\nBuffers:          536870912 kB\nCached:          5368709120 kB\nSwapTotal:       2147483648 kB\nSwapFree:        2147483648 kB\n", "r--r--r--");

        // /var/log files
        AddFile("/var/log/syslog", "", "rw-r-----");
        AddFile("/var/log/auth.log", "", "rw-r-----");
        AddFile("/var/log/dpkg.log", "", "rw-r--r--");
    }

    private void AddNode(string path, VfsNodeType type, string permissions = "rwxr-xr-x", string owner = "root")
    {
        _nodes[NormalizePath(path)] = new VfsNode(path, type)
        {
            Permissions = permissions,
            Owner = owner,
            Group = owner == "root" ? "root" : owner,
            Modified = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 90))
        };
    }

    private void AddFile(string path, string content, string permissions)
    {
        var node = new VfsNode(path, VfsNodeType.File)
        {
            Content = content,
            Permissions = permissions,
            Modified = DateTime.UtcNow.AddDays(-Random.Shared.Next(1, 90))
        };
        _nodes[NormalizePath(path)] = node;
    }

    public bool DirectoryExists(string path)
    {
        path = NormalizePath(path);
        return _nodes.ContainsKey(path) && _nodes[path].Type == VfsNodeType.Directory;
    }

    public bool FileExists(string path)
    {
        path = NormalizePath(path);
        return _nodes.ContainsKey(path) && _nodes[path].Type == VfsNodeType.File;
    }

    public bool Exists(string path)
    {
        path = NormalizePath(path);
        return _nodes.ContainsKey(path);
    }

    public string? ReadFile(string path)
    {
        path = NormalizePath(path);
        return _nodes.TryGetValue(path, out var node) && node.Type == VfsNodeType.File
            ? node.Content
            : null;
    }

    public void CreateFile(string path, string content = "")
    {
        lock (_lock)
        {
            path = NormalizePath(path);
            _nodes[path] = new VfsNode(path, VfsNodeType.File)
            {
                Content = content,
                IsUserCreated = true,
                Modified = DateTime.UtcNow
            };
            _changeLog.Add($"CREATED FILE: {path}");
        }
    }

    public void CreateDirectory(string path)
    {
        lock (_lock)
        {
            path = NormalizePath(path);
            _nodes[path] = new VfsNode(path, VfsNodeType.Directory)
            {
                IsUserCreated = true,
                Modified = DateTime.UtcNow
            };
            _changeLog.Add($"CREATED DIR: {path}");
        }
    }

    public void DeleteNode(string path)
    {
        lock (_lock)
        {
            path = NormalizePath(path);
            var toRemove = _nodes.Keys.Where(k => k == path || k.StartsWith(path + "/")).ToList();
            foreach (var key in toRemove)
            {
                _nodes.Remove(key);
            }
            _changeLog.Add($"DELETED: {path}");
        }
    }

    public void AppendFile(string path, string content)
    {
        lock (_lock)
        {
            path = NormalizePath(path);
            if (_nodes.TryGetValue(path, out var node) && node.Type == VfsNodeType.File)
            {
                node.Content += content;
                node.Modified = DateTime.UtcNow;
                _changeLog.Add($"APPENDED TO: {path}");
            }
        }
    }

    public string[] ListDirectory(string path)
    {
        path = NormalizePath(path);
        var prefix = path == "/" ? "/" : path + "/";

        return _nodes.Keys
            .Where(k => k != path && k.StartsWith(prefix))
            .Select(k =>
            {
                var relative = k[prefix.Length..];
                var slashIdx = relative.IndexOf('/');
                return slashIdx >= 0 ? relative[..slashIdx] : relative;
            })
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
    }

    public string ResolvePath(string cwd, string path)
    {
        if (string.IsNullOrEmpty(path)) return cwd;

        if (!path.StartsWith('/'))
        {
            path = cwd == "/" ? "/" + path : cwd + "/" + path;
        }

        // Resolve . and ..
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.Pop();
            }
            else
            {
                stack.Push(part);
            }
        }

        var resolved = "/" + string.Join("/", stack.Reverse());
        return resolved;
    }

    /// <summary>
    /// Returns a summary of filesystem changes for AI context
    /// </summary>
    public string GetChangesSummary()
    {
        lock (_lock)
        {
            if (_changeLog.Count == 0) return "(no changes)";
            return string.Join("\n", _changeLog.TakeLast(50));
        }
    }

    /// <summary>
    /// Returns user-created/modified files for AI context
    /// </summary>
    public string GetUserFilesInfo()
    {
        lock (_lock)
        {
            var userNodes = _nodes.Values.Where(n => n.IsUserCreated).ToList();
            if (userNodes.Count == 0) return "(none)";
            return string.Join("\n", userNodes.Select(n =>
                $"  {(n.Type == VfsNodeType.Directory ? "d" : "-")}{n.Permissions} {n.Owner} {n.Path}" +
                (n.Type == VfsNodeType.File && n.Content.Length > 0 ? $" ({n.Content.Length} bytes)" : "")));
        }
    }

    /// <summary>
    /// Apply filesystem side effects from a command (simple heuristic parsing)
    /// </summary>
    public void ApplyCommandSideEffects(string command, string cwd)
    {
        lock (_lock)
        {
            var parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var cmd = parts[0];

            try
            {
                switch (cmd)
                {
                    case "touch" when parts.Length >= 2:
                        for (int i = 1; i < parts.Length; i++)
                        {
                            var p = ResolvePath(cwd, parts[i]);
                            if (!Exists(p)) CreateFile(p);
                        }
                        break;
                    case "mkdir" when parts.Length >= 2:
                        var mkdirStart = parts.Contains("-p") ? 1 : 1;
                        for (int i = mkdirStart; i < parts.Length; i++)
                        {
                            if (parts[i].StartsWith("-")) continue;
                            var p = ResolvePath(cwd, parts[i]);
                            CreateDirectory(p);
                        }
                        break;
                    case "rm" when parts.Length >= 2:
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (parts[i].StartsWith("-")) continue;
                            var p = ResolvePath(cwd, parts[i]);
                            if (Exists(p)) DeleteNode(p);
                        }
                        break;
                    case "rmdir" when parts.Length >= 2:
                        for (int i = 1; i < parts.Length; i++)
                        {
                            if (parts[i].StartsWith("-")) continue;
                            var p = ResolvePath(cwd, parts[i]);
                            if (DirectoryExists(p)) DeleteNode(p);
                        }
                        break;
                }

                // Handle redirects: > and >>
                var redirectIdx = command.IndexOf(">>");
                if (redirectIdx >= 0)
                {
                    var target = command[(redirectIdx + 2)..].Trim().Split(' ')[0];
                    if (!string.IsNullOrEmpty(target))
                    {
                        var p = ResolvePath(cwd, target);
                        if (!Exists(p)) CreateFile(p);
                    }
                }
                else
                {
                    redirectIdx = command.IndexOf('>');
                    if (redirectIdx >= 0 && (redirectIdx == 0 || command[redirectIdx - 1] != '2'))
                    {
                        var target = command[(redirectIdx + 1)..].Trim().Split(' ')[0];
                        if (!string.IsNullOrEmpty(target))
                        {
                            var p = ResolvePath(cwd, target);
                            CreateFile(p);
                        }
                    }
                }
            }
            catch
            {
                // Ignore parse errors
            }
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        path = path.Replace('\\', '/');
        if (path != "/" && path.EndsWith('/')) path = path[..^1];
        return path;
    }
}
