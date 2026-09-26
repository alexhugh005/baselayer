#!/usr/bin/env bash
#
# create_ssh_user.sh — create a user with key-based SSH access and a scripts directory.
# Target: Ubuntu 24.04 LTS. Run as root (sudo).
#
# Usage:
#   sudo ./create_ssh_user.sh -u <username> [-k "<public key>" | -f <pubkey file>] [-s] [-p]
#   sudo ./create_ssh_user.sh -u <username> -d [-y]
#
# Options:
#   -u  Username to create or delete (required)
#   -k  SSH public key string, e.g. "ssh-ed25519 AAAA... you@host"
#   -f  Path to a file containing one or more SSH public keys
#       (no -k or -f: a new ed25519 key pair is generated for the user)
#   -p  Print the generated private key to the terminal (only with a generated key)
#   -s  Also add the user to the sudo group
#   -d  Delete the user, their home directory (incl. scripts), mail spool and saved keys
#   -y  Skip the confirmation prompt when deleting
#   -h  Show this help
#
# Result (create):
#   /home/<user>/.ssh/authorized_keys   (700 / 600, owned by user)
#   /home/<user>/scripts                (on the user's PATH via ~/.profile)
#   /root/ssh-keys/<user>/id_ed25519    (generated private key, root-only; hand it to the user)
#   Password login disabled for the user; SSH key login only.

set -euo pipefail

usage() { sed -n '3,25p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die()   { echo "Error: $*" >&2; exit 1; }
log()   { echo "==> $*"; }

KEY_STORE="/root/ssh-keys"
USERNAME=""
PUBKEY=""
KEYFILE=""
GRANT_SUDO=false
DELETE=false
ASSUME_YES=false
PRINT_KEY=false
GENERATED_KEY=""

while getopts ":u:k:f:psdyh" opt; do
  case "$opt" in
    u) USERNAME="$OPTARG" ;;
    k) PUBKEY="$OPTARG" ;;
    f) KEYFILE="$OPTARG" ;;
    p) PRINT_KEY=true ;;
    s) GRANT_SUDO=true ;;
    d) DELETE=true ;;
    y) ASSUME_YES=true ;;
    h) usage 0 ;;
    :) die "Option -$OPTARG requires a value." ;;
    *) die "Unknown option -$OPTARG. Use -h for help." ;;
  esac
done

# --- Checks -----------------------------------------------------------------
[[ $EUID -eq 0 ]] || die "Run as root (sudo)."
[[ -n "$USERNAME" ]] || { echo "Error: -u <username> is required." >&2; usage 1; }
[[ "$USERNAME" =~ ^[a-z_][a-z0-9_-]{0,31}$ ]] \
  || die "Invalid username '$USERNAME' (lowercase letters, digits, _ or -, max 32 chars)."

# --- Delete mode -------------------------------------------------------------
if $DELETE; then
  if [[ -n "$PUBKEY" || -n "$KEYFILE" ]] || $GRANT_SUDO || $PRINT_KEY; then
    die "-d cannot be combined with -k, -f, -s or -p."
  fi
  id "$USERNAME" >/dev/null 2>&1 || die "User '$USERNAME' does not exist."

  # Guard rails: never remove system accounts, root, or the admin running this
  DEL_UID="$(id -u "$USERNAME")"
  [[ "$USERNAME" != "root" && "$DEL_UID" -ge 1000 && "$DEL_UID" -ne 65534 ]] \
    || die "Refusing to delete system account '$USERNAME' (uid $DEL_UID)."
  [[ "$USERNAME" != "${SUDO_USER:-}" ]] \
    || die "Refusing to delete '$USERNAME': that's the account running this script."

  DEL_HOME="$(getent passwd "$USERNAME" | cut -d: -f6)"
  if ! $ASSUME_YES; then
    [[ -t 0 ]] || die "No terminal for confirmation; re-run with -y to delete non-interactively."
    read -r -p "Delete user '$USERNAME' and ALL files in $DEL_HOME? Type the username to confirm: " reply
    [[ "$reply" == "$USERNAME" ]] || die "Confirmation did not match; nothing deleted."
  fi

  if pgrep -u "$USERNAME" >/dev/null 2>&1; then
    log "Ending processes owned by '$USERNAME'"
    loginctl terminate-user "$USERNAME" >/dev/null 2>&1 || true
    pkill -KILL -u "$USERNAME" || true
    sleep 1
  fi

  log "Deleting user '$USERNAME' and $DEL_HOME"
  crontab -r -u "$USERNAME" >/dev/null 2>&1 || true
  deluser --remove-home "$USERNAME" >/dev/null 2>&1 || userdel -r "$USERNAME"
  id "$USERNAME" >/dev/null 2>&1 && die "User '$USERNAME' still exists; check 'deluser' output."
  if [[ -d "$KEY_STORE/$USERNAME" ]]; then
    rm -rf -- "${KEY_STORE:?}/$USERNAME"
    log "Removed saved keys in $KEY_STORE/$USERNAME"
  fi
  echo
  echo "Done. User '$USERNAME' removed."
  exit 0
fi

if [[ -n "$PUBKEY" && -n "$KEYFILE" ]]; then
  die "Use either -k or -f, not both."
elif [[ -n "$KEYFILE" ]]; then
  [[ -r "$KEYFILE" ]] || die "Key file '$KEYFILE' not readable."
  PUBKEY="$(grep -Ev '^\s*(#|$)' "$KEYFILE")"
elif [[ -z "$PUBKEY" ]]; then
  # No key supplied: generate one (reuse it on re-runs so existing logins keep working)
  GENERATED_KEY="$KEY_STORE/$USERNAME/id_ed25519"
  install -d -m 700 "$KEY_STORE" "$KEY_STORE/$USERNAME"
  if [[ -f "$GENERATED_KEY" ]]; then
    log "Reusing previously generated key $GENERATED_KEY"
  else
    log "Generating ed25519 key pair for '$USERNAME'"
    ssh-keygen -q -t ed25519 -N "" -C "${USERNAME}@$(hostname)" -f "$GENERATED_KEY"
  fi
  chmod 600 "$GENERATED_KEY"
  PUBKEY="$(cat "$GENERATED_KEY.pub")"
fi

if $PRINT_KEY && [[ -z "$GENERATED_KEY" ]]; then
  die "-p only applies to a generated key; you supplied your own with -k/-f."
fi

# Validate every key line
while IFS= read -r line; do
  [[ -z "$line" ]] && continue
  printf '%s\n' "$line" | ssh-keygen -l -f /dev/stdin >/dev/null 2>&1 \
    || die "Invalid SSH public key: ${line:0:40}..."
done <<< "$PUBKEY"

# --- SSH server --------------------------------------------------------------
if ! dpkg -s openssh-server >/dev/null 2>&1; then
  log "Installing openssh-server"
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq openssh-server
fi
# Ubuntu 24.04 uses socket activation (ssh.socket) by default
if ! systemctl enable --now ssh.socket >/dev/null 2>&1 \
   && ! systemctl enable --now ssh >/dev/null 2>&1; then
  echo "Warning: could not enable the SSH service; start it manually (systemctl start ssh)." >&2
fi

# --- User --------------------------------------------------------------------
if id "$USERNAME" >/dev/null 2>&1; then
  log "User '$USERNAME' already exists; updating SSH keys and directories"
else
  log "Creating user '$USERNAME'"
  adduser --disabled-password --gecos "" --shell /bin/bash "$USERNAME" >/dev/null
fi

if $GRANT_SUDO; then
  log "Adding '$USERNAME' to sudo group"
  usermod -aG sudo "$USERNAME"
fi

HOME_DIR="$(getent passwd "$USERNAME" | cut -d: -f6)"
USER_GROUP="$(id -gn "$USERNAME")"

# --- SSH keys ----------------------------------------------------------------
SSH_DIR="$HOME_DIR/.ssh"
AUTH_KEYS="$SSH_DIR/authorized_keys"
install -d -m 700 -o "$USERNAME" -g "$USER_GROUP" "$SSH_DIR"
touch "$AUTH_KEYS"
while IFS= read -r line; do
  [[ -z "$line" ]] && continue
  grep -qxF "$line" "$AUTH_KEYS" || printf '%s\n' "$line" >> "$AUTH_KEYS"
done <<< "$PUBKEY"
chown "$USERNAME:$USER_GROUP" "$AUTH_KEYS"
chmod 600 "$AUTH_KEYS"
log "Installed $(grep -c . "$AUTH_KEYS") key(s) in $AUTH_KEYS"

# --- Scripts directory -------------------------------------------------------
SCRIPTS_DIR="$HOME_DIR/scripts"
install -d -m 750 -o "$USERNAME" -g "$USER_GROUP" "$SCRIPTS_DIR"

PROFILE="$HOME_DIR/.profile"
# shellcheck disable=SC2016  # expanded at login, not now
PATH_LINE='[ -d "$HOME/scripts" ] && PATH="$HOME/scripts:$PATH"'
touch "$PROFILE"
grep -qxF "$PATH_LINE" "$PROFILE" || printf '\n# User scripts\n%s\n' "$PATH_LINE" >> "$PROFILE"
chown "$USERNAME:$USER_GROUP" "$PROFILE"
log "Created $SCRIPTS_DIR (added to PATH)"

# --- Done --------------------------------------------------------------------
HOST_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
TARGET="${USERNAME}@${HOST_IP:-<server-ip>}"
echo
if [[ -n "$GENERATED_KEY" ]]; then
  echo "Done. Private key for '$USERNAME' saved to $GENERATED_KEY (root-only)."
  if $PRINT_KEY; then
    echo
    echo "----- private key (save as ~/.ssh/${USERNAME}_ed25519 on the client) -----"
    cat "$GENERATED_KEY"
    echo "-------------------------------------------------------------------------"
  fi
  echo
  echo "On the client machine:"
  echo "  1. Copy the private key over, e.g. from the server:  sudo cat $GENERATED_KEY"
  echo "     and save it as ~/.ssh/${USERNAME}_ed25519"
  echo "  2. chmod 600 ~/.ssh/${USERNAME}_ed25519"
  echo "  3. ssh -i ~/.ssh/${USERNAME}_ed25519 $TARGET"
  echo
  echo "Once the user has the key, you can delete the server copy:  sudo rm -r $KEY_STORE/$USERNAME"
else
  echo "Done. Connect with:"
  echo "  ssh $TARGET"
fi
