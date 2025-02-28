#!/bin/sh
# Copy the filebeat.yml file from a temporary location to the desired location
cp /tmp/filebeat.yml /usr/share/filebeat/filebeat.yml

# Change the permissions of the file
chmod 644 /usr/share/filebeat/filebeat.yml

# Start Filebeat
exec filebeat -e --strict.perms=false