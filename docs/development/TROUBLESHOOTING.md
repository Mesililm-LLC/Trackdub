# Troubleshooting Guide

> Current AWS POC contract: [SnapSoft application acceptance](../operations/snapsoft-poc-acceptance.md). Historical API/Swagger/compose/telemetry examples below are not evidence of current wiring. The new CPU API uses DynamoDB/S3, contains no inference, and has no billing/OTel exporter. GPU compose runs the one-shot CLI; it does not expose HTTP.

> Current AWS POC contract: [SnapSoft application acceptance](../operations/snapsoft-poc-acceptance.md). Historical API/Swagger/compose/telemetry examples below are not evidence of current wiring. The new CPU API uses DynamoDB/S3, contains no inference, and has no billing/OTel exporter. GPU compose runs the one-shot CLI; it does not expose HTTP.

## Common Issues

### API Container Won't Start

**Symptoms:** Container exits immediately with exit code 1.

**Diagnosis:**
```bash
docker compose -f docker-compose.api.yml logs trackdub-api
```

**Common causes:**

1. **Port 8080 already in use**
   ```bash
   # Find what's using port 8080
   lsof -i :8080  # macOS/Linux
   netstat -ano | findstr :8080  # Windows
   
   # Solution: Change port in docker-compose.api.yml
   # ports:
   #   - "8081:8080"
   ```

2. **Missing environment variables**
   ```bash
   # Check .env file exists
   test -f .env || cp .env.example .env
   ```

3. **Model download timeout**
   ```bash
   # Increase timeout in .env
   MODEL_DOWNLOAD_TIMEOUT_SECONDS=600
   
   # Or disable auto-download
   AUTO_DOWNLOAD_MODELS=false
   ```

4. **Database locked or corrupted**
   ```bash
   # Reset database
   rm data/trackdub.db
   docker compose -f docker-compose.api.yml restart
   ```

---

### Health Check Failing

**Symptoms:** Health check endpoint returns 503 or connection refused.

**Diagnosis:**
```bash
curl -v http://localhost:8080/api/health/ready
```

**Common causes:**

1. **Service not fully initialized** (wait 15-30 seconds)
   ```bash
   docker compose -f docker-compose.api.yml logs -f trackdub-api | grep -i 'ready\|initialized'
   ```

2. **Models still downloading**
   ```bash
   docker compose -f docker-compose.api.yml logs | grep -i 'download\|model'
   ```

3. **Database not accessible**
   ```bash
   # Check database path
   ls -la data/trackdub.db
   
   # Rebuild database
   rm data/trackdub.db
   docker compose -f docker-compose.api.yml exec trackdub-api curl http://localhost:8080/api/health/ready
   ```

---

### High Disk Usage

**Symptoms:** `df -h` shows `/` partition nearly full.

**Diagnosis:**
```bash
du -sh data/*
docker system df
```

**Common causes:**

1. **Large model cache**
   ```bash
   du -sh data/models/
   
   # Clear cache (will re-download on next run)
   rm -rf data/models/*
   ```

2. **Old Docker images/layers**
   ```bash
   docker system prune -a --volumes
   ```

3. **Log files accumulating**
   ```bash
   du -sh data/logs/
   
   # Clear old logs
   find data/logs -type f -mtime +7 -delete
   ```

---

### Out of Memory

**Symptoms:** OOM killer terminates container; logs show `Killed` or `Aborted`.

**Solution:**

```bash
# Check memory usage
docker stats trackdub-api

# Increase Docker memory limit
# Option 1: Docker Desktop UI
#   Preferences > Resources > Memory: 8GB

# Option 2: Edit docker-compose.api.yml
# services:
#   trackdub-api:
#     mem_limit: 8g

docker compose -f docker-compose.api.yml down
docker compose -f docker-compose.api.yml up -d
```

---

### Network/Connectivity Issues

**Symptoms:** Service runs but can't reach external resources (models, APIs).

**Diagnosis:**
```bash
docker compose -f docker-compose.api.yml exec trackdub-api curl -v https://huggingface.co
```

**Common causes:**

1. **DNS resolution failing**
   ```bash
   docker compose -f docker-compose.api.yml exec trackdub-api nslookup huggingface.co
   
   # Add DNS in docker-compose.api.yml:
   # services:
   #   trackdub-api:
   #     dns:
   #       - 8.8.8.8
   #       - 1.1.1.1
   ```

2. **Firewall blocking outbound traffic**
   ```bash
   # Test connectivity
   docker compose -f docker-compose.api.yml exec trackdub-api curl -I https://api.github.com
   ```

3. **Proxy required**
   ```bash
   # Add to docker-compose.api.yml environment:
   HTTP_PROXY: http://proxy.example.com:3128
   HTTPS_PROXY: http://proxy.example.com:3128
   NO_PROXY: localhost,127.0.0.1
   ```

---

### API Endpoints Not Responding

**Symptoms:** `curl http://localhost:8080/api/...` times out or refuses connection.

**Diagnosis:**
```bash
# Check if container is running
docker ps | grep trackdub-api

# Check port binding
docker port trackdub-api

# Test from container
docker compose -f docker-compose.api.yml exec trackdub-api curl http://localhost:8080/api/health/live
```

**Common causes:**

1. **Container running but port not exposed**
   ```bash
   # Verify port mapping in docker-compose.api.yml
   ports:
     - "8080:8080"
   
   docker compose -f docker-compose.api.yml down
   docker compose -f docker-compose.api.yml up -d
   ```

2. **Firewall blocking port 8080**
   ```bash
   # macOS
   sudo lsof -i :8080
   
   # Linux
   sudo netstat -tlnp | grep 8080
   ```

---

### Slow Performance / High Latency

**Symptoms:** API requests take >5 seconds; inference tasks slow.

**Diagnosis:**
```bash
docker stats trackdub-api
docker compose -f docker-compose.api.yml logs | grep -i 'cpu\|memory\|inference'
```

**Solutions:**

1. **Enable GPU acceleration** (if available)
   ```bash
   # Use nvidia-docker and GPU Dockerfile
   docker compose -f docker-compose.api.yml -f docker-compose.api.gpu.yml up -d
   ```

2. **Increase CPU allocation**
   ```yaml
   services:
     trackdub-api:
       cpus: 4  # Default: no limit
   ```

3. **Pre-warm models** (load on startup)
   ```bash
   # Add to .env
   PRELOAD_MODELS=true
   ```

4. **Check disk I/O** (slow volume mounts)
   ```bash
   docker exec trackdub-api iostat -dx 1 5
   ```

---

### Database Errors

**Symptoms:** `SqliteException`, `database is locked`, or schema migration errors.

**Diagnosis:**
```bash
ls -la data/trackdub.db
docker compose -f docker-compose.api.yml exec trackdub-api sqlite3 /data/trackdub.db ".schema"
```

**Solutions:**

1. **Database locked**
   ```bash
   # Restart service
   docker compose -f docker-compose.api.yml restart
   ```

2. **Corrupted database**
   ```bash
   # Backup and reset
   cp data/trackdub.db data/trackdub.db.backup
   rm data/trackdub.db
   docker compose -f docker-compose.api.yml restart
   ```

3. **Schema mismatch**
   ```bash
   # Check database version
   docker compose -f docker-compose.api.yml exec trackdub-api sqlite3 /data/trackdub.db "SELECT * FROM __EFMigrationsHistory;"
   
   # Re-initialize
   rm data/trackdub.db
   docker compose -f docker-compose.api.yml exec trackdub-api dotnet Trackdub.Api.dll --init-db
   ```

---

### Model Cache Issues

**Symptoms:** Models fail to download; version mismatches; cache not being used.

**Diagnosis:**
```bash
ls -la data/models/
du -sh data/models/

docker compose -f docker-compose.api.yml logs | grep -i 'download\|cache\|model'
```

**Solutions:**

1. **Force re-download**
   ```bash
   rm -rf data/models/*
   docker compose -f docker-compose.api.yml restart
   ```

2. **Check model manifest**
   ```bash
   cat bundled-models.manifest.json | jq .
   ```

3. **Increase download timeout**
   ```bash
   # Edit .env
   MODEL_DOWNLOAD_TIMEOUT_SECONDS=600
   ```

---

### Logs Not Appearing

**Symptoms:** `docker compose logs` is empty or not updating.

**Solutions:**

1. **Check logging configuration**
   ```bash
   docker compose -f docker-compose.api.yml exec trackdub-api cat appsettings.json | grep -i logging
   ```

2. **Enable verbose logging**
   ```bash
   # Edit .env
   LOGGING_LEVEL=Debug
   ```

3. **View container stderr/stdout**
   ```bash
   docker compose -f docker-compose.api.yml logs -f --tail=100
   ```

---

## Getting Help

- **GitHub Issues:** [Report a bug](https://github.com/Babelworks/Trackdub/issues/new)
- **Discussions:** [Ask a question](https://github.com/Babelworks/Trackdub/discussions)
- **Logs:** Always include output from `docker compose logs -f trackdub-api` (sanitize secrets first)
- **System info:**
  ```bash
  docker version
  docker compose version
  df -h
  free -h  # or `vm_stat` on macOS
  ```
