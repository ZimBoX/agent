# Развёртывание бэкенда на сервере (Docker Compose)

Целевой сервер: Ubuntu 24.04, `zbx@192.168.0.15`, доступ по SSH-ключу.
Бэкенд и Postgres поднимаются двумя контейнерами. Клиент ходит на бэкенд по
`http://192.168.0.15:5149` (plain HTTP, только локальная сеть — TLS добавим позже).

> **Не работает после переезда на Linux:** «поиск файлов по объекту» (модель сама
> читает файлы из папки на сетевом ресурсе). Бэкенд открывает UNC-пути напрямую —
> на Linux нужна отдельная настройка (монтирование SMB-шар + трансляция путей).
> Осознанно отложено. Всё остальное (чат, вложения, админка, автообновление
> клиента) работает.

## 1. Установить Docker на сервере (однократно)

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
# чтобы работать docker без sudo:
sudo usermod -aG docker zbx    # затем перелогиниться (новый ssh-сеанс)
```

## 2. Забрать код и настроить секреты

```bash
git clone https://github.com/ZimBoX/agent.git ~/agent
cd ~/agent/deploy
cp .env.example .env
nano .env        # вписать AITUNNEL_API_KEY и сменить ADMIN_PASSWORD
```

## 3. Поднять

```bash
cd ~/agent/deploy
docker compose up -d --build
docker compose ps          # оба сервиса healthy/up
docker compose logs -f backend
```

Готово, когда в логах бэкенда `Now listening on: http://0.0.0.0:5149` и миграции
применились без ошибок. Проверка:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5149/admin/login   # 200
```

С другой машины в сети: `http://192.168.0.15:5149/admin`.

## 4. Обновление на новую версию кода

```bash
cd ~/agent && git pull
cd deploy && docker compose up -d --build
```

Миграции накатываются автоматически при старте бэкенда.

## 5. Клиент (отдельный шаг, на Windows-машине)

Клиент по умолчанию собран на `http://localhost:5149`. Чтобы он ходил на сервер,
пересобрать и переустановить с адресом сервера:

- [client/ClaudeAssistant.Client/appsettings.json](../client/ClaudeAssistant.Client/appsettings.json):
  `"BackendBaseUrl": "http://192.168.0.15:5149"`
- [InstallerLauncher/Program.cs](../client/ClaudeAssistant.Client.InstallerLauncher/Program.cs)
  (тонкий установщик качает билд с бэкенда): `backendBaseUrl` → тот же адрес.

Затем собрать клиент через **VS MSBuild** (не `dotnet build` — баг с `.pri`, см.
`ПЕРЕЕЗД.md`) и залить билд через админку `/admin`.

## Бэкапы БД (по плану, Этап 12)

```bash
docker compose exec -T postgres pg_dump -U postgres claude_assistant | gzip > backup-$(date +%F).sql.gz
```

Позже — вынести в cron в отдельное от сервера место.
