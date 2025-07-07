#!/usr/bin/env bash

docker buildx build --platform linux/amd64 \
    -t zanfranceschi/rinha-de-backend-2025-csharp-exemplo ../src/rinha-de-backend-2025
docker push zanfranceschi/rinha-de-backend-2025-csharp-exemplo
