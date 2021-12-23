LOG_PATH="data/logs/*.logs.txt"

tailLogs() {
	tail -f $HOME/$LOG_PATH
}

tailLogs