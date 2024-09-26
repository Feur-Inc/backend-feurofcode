#!/bin/bash

while true
do
    ping -c 8 8.8.8.8 > /dev/null 2>&1
    if [ $? -eq 0 ]; then
        echo "$(date) : Successful ping to 8.8.8.8"
    else
        echo "$(date) : Failed ping to 8.8.8.8"
    fi
    sleep 600
done
