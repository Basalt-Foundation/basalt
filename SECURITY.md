# Security Policy

## Reporting a Vulnerability

The Basalt Foundation takes security seriously. If you discover a security vulnerability, we appreciate your responsible disclosure.

### How to Report

**Do not open a public GitHub issue for security vulnerabilities.**

Instead, use [GitHub's private vulnerability reporting](https://github.com/Basalt-Foundation/basalt/security/advisories/new) to submit your report directly through the repository.

Include the following in your report:

- Description of the vulnerability
- Steps to reproduce
- Potential impact
- Suggested fix (if any)

### Response Timeline

- **Acknowledgment**: Within 48 hours
- **Initial assessment**: Within 1 week
- **Fix timeline**: Depends on severity, typically within 30 days for critical issues

### Scope

The following are in scope for security reports:

- Consensus protocol vulnerabilities
- Cryptographic implementation flaws
- P2P network attacks (eclipse, Sybil, etc.)
- Smart contract VM escapes
- Bridge security issues
- State corruption or manipulation
- Denial of service vectors
- Authentication/authorization bypasses in APIs

### Out of Scope

- Vulnerabilities in dependencies (report upstream)
- Social engineering attacks
- Physical security
- Issues in third-party services

### Recognition

We maintain a security acknowledgments page for researchers who responsibly disclose vulnerabilities. If you would like to be credited, please let us know in your report.

## Supported Versions

| Version | Supported |
|---------|-----------|
| Latest  | Yes       |

## Security Best Practices for Node Operators

- Run validators behind a firewall
- Use dedicated machines for validator nodes
- Keep the .NET runtime and system packages updated
- Monitor validator logs for unusual activity
- Back up validator keys securely and offline
- Use the sandboxed contract runtime in production (`BASALT_USE_SANDBOX=true`)
