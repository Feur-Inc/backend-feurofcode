#!/bin/bash

# Liste des dossiers à surveiller
MONITORED_DIRS=(
  "/root/bash"
  "/root/python"
  "/root/nb"
)

MAX_SIZE=10485760  # 10 MB en octets

while true; do
  for dir in "${MONITORED_DIRS[@]}"; do
    if [ -d "$dir" ]; then
      find "$dir" -type f | while read -r file; do
        FILE_SIZE=$(stat -c%s "$file")
        if [ "$FILE_SIZE" -gt "$MAX_SIZE" ]; then
          echo "Deleting $file ($FILE_SIZE bytes)"
          rm "$file"
        fi
      done
    else
      echo "Directory $dir does not exist."
    fi
  done
  sleep 0.5
done
