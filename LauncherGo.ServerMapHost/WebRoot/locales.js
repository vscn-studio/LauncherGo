/* Additional LauncherGo languages for the map's common controls.
 * Advanced/admin messages not listed here deliberately fall back to English.
 * Columns: Russian, German, French, Spanish, Polish, Brazilian Portuguese. */
(() => {
  'use strict';
  const languages = ['ru', 'de', 'fr', 'es', 'pl', 'pt'];
  const rows = {
    languageName: ['Язык','Sprache','Langue','Idioma','Język','Idioma'],
    menu: ['Меню','Menü','Menu','Menú','Menu','Menu'],
    announcement: ['Объявления','Neuigkeiten','Annonces','Anuncios','Ogłoszenia','Avisos'],
    pointActions: ['Координаты','Koordinaten','Coordonnées','Coordenadas','Współrzędne','Coordenadas'],
    mapTypes: ['Тип карты','Kartentyp','Type de carte','Tipo de mapa','Typ mapy','Tipo de mapa'],
    colorMap: ['Цветная карта','Farbige Karte','Carte en couleur','Mapa en color','Mapa kolorowa','Mapa colorido'],
    plainMap: ['Карта сепия','Sepiakarte','Carte sépia','Mapa sepia','Mapa sepia','Mapa sépia'],
    layers: ['Слои','Ebenen','Calques','Capas','Warstwy','Camadas'],
    onlinePlayers: ['Игроки онлайн','Spieler online','Joueurs en ligne','Jugadores en línea','Gracze online','Jogadores online'],
    noPlayers: ['Нет игроков онлайн','Keine Spieler online','Aucun joueur en ligne','No hay jugadores en línea','Brak graczy online','Nenhum jogador online'],
    login: ['Войти','Anmelden','Connexion','Iniciar sesión','Zaloguj się','Entrar'],
    register: ['Регистрация','Registrieren','Inscription','Registrarse','Zarejestruj się','Cadastrar'],
    manage: ['Управление','Verwalten','Gérer','Administrar','Zarządzaj','Gerenciar'],
    logout: ['Выйти','Abmelden','Déconnexion','Cerrar sesión','Wyloguj się','Sair'],
    loginTitle: ['Вход на карту','Kartenanmeldung','Connexion à la carte','Acceso al mapa','Logowanie do mapy','Entrar no mapa'],
    registerTitle: ['Регистрация на карте','Kartenkonto registrieren','Créer un compte pour la carte','Crear cuenta del mapa','Rejestracja konta mapy','Criar conta do mapa'],
    registerHelp: ['Введите эту команду в игре, чтобы задать или сбросить пароль:','Führe diesen Befehl im Spiel aus, um dein Passwort festzulegen oder zurückzusetzen:','Exécutez cette commande en jeu pour définir ou réinitialiser votre mot de passe :','Ejecuta este comando en el juego para establecer o restablecer tu contraseña:','Wpisz to polecenie w grze, aby ustawić lub zresetować hasło:','Execute este comando no jogo para definir ou redefinir sua senha:'],
    registerHelp2: ['Затем введите имя игрока и пароль в окне входа.','Gib anschließend deinen Spielernamen und dein Passwort im Anmeldedialog ein.','Saisissez ensuite votre nom de joueur et votre mot de passe dans la fenêtre de connexion.','Después introduce tu nombre de jugador y contraseña en el diálogo de acceso.','Następnie wpisz nazwę gracza i hasło w oknie logowania.','Depois, informe seu nome de jogador e sua senha na janela de login.'],
    playerName: ['Имя игрока','Spielername','Nom du joueur','Nombre del jugador','Nazwa gracza','Nome do jogador'],
    password: ['Пароль','Passwort','Mot de passe','Contraseña','Hasło','Senha'],
    cancel: ['Отмена','Abbrechen','Annuler','Cancelar','Anuluj','Cancelar'],
    close: ['Закрыть','Schließen','Fermer','Cerrar','Zamknij','Fechar'],
    save: ['Сохранить','Speichern','Enregistrer','Guardar','Zapisz','Salvar'],
    notLoggedIn: ['Вход не выполнен','Nicht angemeldet','Non connecté','Sin iniciar sesión','Nie zalogowano','Não conectado'],
    loggedIn: ['Вход выполнен','Angemeldet','Connecté','Sesión iniciada','Zalogowano','Conectado'],
    search: ['Поиск','Suchen','Rechercher','Buscar','Szukaj','Buscar'],
    searchPlaceholder: ['Поиск игроков, меток, маршрутов, мест','Spieler, Markierungen, Routen und Orte suchen','Rechercher joueurs, repères, itinéraires, lieux','Buscar jugadores, marcadores, rutas y lugares','Szukaj graczy, znaczników, tras i miejsc','Buscar jogadores, marcadores, rotas e locais'],
    noResults: ['Совпадений нет','Keine Treffer','Aucun résultat','Sin resultados','Brak wyników','Nenhum resultado'],
    locationHidden: ['Для просмотра позиции войдите','Position erfordert Anmeldung','Connexion requise pour voir la position','Inicia sesión para ver la posición','Zaloguj się, aby zobaczyć pozycję','Entre para ver a posição'],
    planRoute: ['Добавить трек на карту','Kartentrack hinzufügen','Ajouter un tracé sur la carte','Añadir trazado al mapa','Dodaj ślad na mapie','Adicionar traçado ao mapa'],
    plan: ['Новый трек','Neuer Track','Nouveau tracé','Nuevo trazado','Nowy ślad','Novo traçado'],
    measureDistance: ['Планировщик маршрутов через транслокаторы','Translokator-Routenplanung','Planifier un itinéraire par translocateurs','Planificar ruta por translocalizadores','Planowanie trasy przez translokatory','Planejar rota por translocadores'],
    locateSpawn: ['К точке появления','Zum Spawnpunkt','Aller au point de réapparition','Ir al punto de aparición','Przejdź do punktu odrodzenia','Ir ao ponto de surgimento'],
    nearestTranslocator: ['Ближайший транслокатор','Nächster Translokator','Translocateur le plus proche','Translocalizador más cercano','Najbliższy translokator','Translocador mais próximo'],
    totalDistance: ['Всего','Gesamt','Total','Total','Łącznie','Total'],
    segments: ['участков','Abschnitte','segments','tramos','odcinki','trechos'],
    blocks: ['блоков','Blöcke','blocs','bloques','bloków','blocos'],
    finish: ['Готово','Fertig','Terminer','Finalizar','Zakończ','Concluir'],
    clear: ['Очистить','Leeren','Effacer','Borrar','Wyczyść','Limpar'],
    go: ['Перейти','Los','Aller','Ir','Przejdź','Ir'],
    centerHere: ['Центрировать здесь','Hier zentrieren','Centrer ici','Centrar aquí','Wyśrodkuj tutaj','Centralizar aqui'],
    copyCoords: ['Копировать координаты','Koordinaten kopieren','Copier les coordonnées','Copiar coordenadas','Kopiuj współrzędne','Copiar coordenadas'],
    copyWaypoint: ['Копировать команду путевой точки','Wegpunktbefehl kopieren','Copier la commande de repère','Copiar comando de punto de referencia','Kopiuj polecenie punktu trasy','Copiar comando de ponto de referência'],
    addPoi: ['Добавить метку места','Ortsmarkierung hinzufügen','Ajouter un repère de lieu','Añadir marcador de lugar','Dodaj znacznik miejsca','Adicionar marcador de local'],
    poiName: ['Название','Name','Nom','Nombre','Nazwa','Nome'],
    poiText: ['Описание','Beschreibung','Description','Descripción','Opis','Descrição'],
    poiColor: ['Цвет','Farbe','Couleur','Color','Kolor','Cor'],
    savePoi: ['Сохранить метку','Markierung speichern','Enregistrer le repère','Guardar marcador','Zapisz znacznik','Salvar marcador'],
    level: ['Масштаб','Zoom','Zoom','Zoom','Powiększenie','Zoom'],
    blocksPixel: ['блоков/пиксель','Blöcke/Pixel','blocs/pixel','bloques/píxel','bloków/piksel','blocos/pixel'],
    blockPixels: ['блок','Block','bloc','bloque','blok','bloco'],
    pixel: ['пикселей','Pixel','pixels','píxeles','pikseli','pixels'],
    height: ['Высота','Höhe','Altitude','Altura','Wysokość','Altura'],
    direction: ['Направление','Richtung','Direction','Dirección','Kierunek','Direção'],
    mode: ['Режим','Modus','Mode','Modo','Tryb','Modo'],
    dataFrom: ['Данные от','Daten von','Données de','Datos de','Dane z','Dados de'],
    websiteInfo: ['Информация о сайте','Website-Info','Informations du site','Información del sitio','Informacje o stronie','Informações do site'],
    assetNoticeTitle: ['Ресурсы и лицензии','Ressourcen und Lizenzen','Ressources et licences','Recursos y licencias','Zasoby i licencje','Recursos e licenças'],
    serverVersion: ['Версия сервера','Serverversion','Version du serveur','Versión del servidor','Wersja serwera','Versão do servidor'],
    mapVersion: ['Версия карты','Kartenversion','Version de la carte','Versión del mapa','Wersja mapy','Versão do mapa'],
    mapSize: ['Размер карты','Kartengröße','Taille de la carte','Tamaño del mapa','Rozmiar mapy','Tamanho do mapa'],
    cacheSize: ['Размер кэша','Cachegröße','Taille du cache','Tamaño de caché','Rozmiar pamięci podręcznej','Tamanho do cache'],
    renderTime: ['Время отрисовки','Renderzeit','Temps de rendu','Tiempo de renderizado','Czas renderowania','Tempo de renderização'],
    serverConfig: ['Настройки сервера','Serverkonfiguration','Configuration du serveur','Configuración del servidor','Konfiguracja serwera','Configuração do servidor'],
    updated: ['Обновлено','Zuletzt aktualisiert','Dernière mise à jour','Última actualización','Ostatnia aktualizacja','Última atualização'],
    'layer.players': ['Игроки','Spieler','Joueurs','Jugadores','Gracze','Jogadores'],
    'layer.spawn': ['Точка появления','Spawnpunkt','Point de réapparition','Punto de aparición','Punkt odrodzenia','Ponto de surgimento'],
    'layer.claims': ['Названия владений','Gebietsnamen','Noms des propriétés','Nombres de parcelas','Nazwy działek','Nomes das propriedades'],
    'layer.claim-areas': ['Владения','Beanspruchte Gebiete','Propriétés','Parcelas','Działki','Propriedades'],
    'layer.chunks': ['Созданные регионы','Generierte Regionen','Régions générées','Regiones generadas','Wygenerowane regiony','Regiões geradas'],
    'layer.translocators': ['Транслокаторы','Translokatoren','Translocateurs','Translocalizadores','Translokatory','Translocadores'],
    'layer.pois': ['Места','Orte','Lieux','Lugares','Miejsca','Locais'],
    'layer.mounts': ['Транспорт и животные','Reittiere und Fahrzeuge','Montures et véhicules','Monturas y vehículos','Wierzchowce i pojazdy','Montarias e veículos'],
    myMarkers: ['Игровые метки','Spielmarkierungen','Repères du jeu','Marcadores del juego','Znaczniki z gry','Marcadores do jogo'],
    myRoutes: ['Мои треки','Meine Tracks','Mes tracés','Mis trazados','Moje ślady','Meus traçados'],
    hiddenRegions: ['Скрытые области','Verborgene Bereiche','Zones masquées','Zonas ocultas','Ukryte obszary','Áreas ocultas'],
    emptyMarkers: ['Нет игровых меток','Keine Spielmarkierungen','Aucun repère du jeu','Sin marcadores del juego','Brak znaczników z gry','Nenhum marcador do jogo'],
    emptyRoutes: ['Нет сохранённых треков','Keine gespeicherten Tracks','Aucun tracé enregistré','Sin trazados guardados','Brak zapisanych śladów','Nenhum traçado salvo'],
    emptyRegions: ['Нет скрытых областей','Keine verborgenen Bereiche','Aucune zone masquée','Sin zonas ocultas','Brak ukrytych obszarów','Nenhuma área oculta'],
    routeName: ['Название трека','Trackname','Nom du tracé','Nombre del trazado','Nazwa śladu','Nome do traçado'],
    undo: ['Отменить','Rückgängig','Annuler','Deshacer','Cofnij','Desfazer'],
    redo: ['Повторить','Wiederherstellen','Rétablir','Rehacer','Ponów','Refazer'],
    trackHelp: ['Левой кнопкой добавляйте точки трека. Отменяйте и повторяйте действия, затем завершите для сохранения.','Linksklick fügt Trackpunkte hinzu. Punkte lassen sich rückgängig machen und wiederherstellen; zum Speichern abschließen.','Cliquez avec le bouton gauche pour ajouter des points au tracé. Annulez ou rétablissez les points, puis terminez pour enregistrer.','Haz clic izquierdo para añadir puntos al trazado. Deshaz o rehace puntos y finaliza para guardar.','Klikaj lewym przyciskiem, aby dodawać punkty śladu. Cofaj i ponawiaj punkty, a następnie zakończ, aby zapisać.','Clique com o botão esquerdo para adicionar pontos ao traçado. Desfaça ou refaça pontos e conclua para salvar.'],
    routeHelp: ['Нажмите на карту, чтобы задать начало маршрута.','Klicke auf die Karte, um den Start festzulegen.','Cliquez sur la carte pour placer le départ.','Haz clic en el mapa para colocar el inicio.','Kliknij mapę, aby ustawić początek trasy.','Clique no mapa para definir o início.'],
    routeStartPlaced: ['Начало задано. Нажмите, чтобы задать цель.','Start gesetzt. Klicke, um das Ziel festzulegen.','Départ placé. Cliquez pour placer la destination.','Inicio colocado. Haz clic para colocar el destino.','Początek ustawiony. Kliknij, aby ustawić cel.','Início definido. Clique para definir o destino.'],
    routeComputing: ['Расчёт маршрута…','Route wird berechnet…','Calcul de l’itinéraire…','Calculando ruta…','Obliczanie trasy…','Calculando rota…'],
    routeNoRoute: ['Не удалось рассчитать маршрут.','Keine Route konnte berechnet werden.','Impossible de calculer un itinéraire.','No se pudo calcular una ruta.','Nie udało się obliczyć trasy.','Não foi possível calcular uma rota.'],
    routeWalking: ['Пешком','Zu Fuß','Marche','A pie','Pieszo','A pé'],
    routeJumps: ['Прыжки','Sprünge','Sauts','Saltos','Skoki','Saltos'],
    routeDirect: ['По прямой','Luftlinie','À vol d’oiseau','En línea recta','W linii prostej','Em linha reta'],
    routeElevation: ['Перепад высот','Höhenunterschied','Dénivelé','Desnivel','Różnica wysokości','Desnível'],
    routeRestart: ['Нажмите, чтобы построить новый маршрут.','Klicke, um eine neue Route zu planen.','Cliquez pour planifier un nouvel itinéraire.','Haz clic para planificar una nueva ruta.','Kliknij, aby zaplanować nową trasę.','Clique para planejar uma nova rota.'],
    routeStart: ['Начало','Start','Départ','Inicio','Początek','Início'],
    routeDestination: ['Цель','Ziel','Destination','Destino','Cel','Destino'],
    editRoute: ['Изменить трек','Track bearbeiten','Modifier le tracé','Editar trazado','Edytuj ślad','Editar traçado'],
    deleteRoute: ['Удалить трек','Track löschen','Supprimer le tracé','Eliminar trazado','Usuń ślad','Excluir traçado'],
    shareRoute: ['Копировать ссылку на трек','Tracklink kopieren','Copier le lien du tracé','Copiar enlace del trazado','Kopiuj link do śladu','Copiar link do traçado'],
    routeMin: ['Нужно минимум две точки','Mindestens zwei Punkte erforderlich','Au moins deux points sont requis','Se necesitan al menos dos puntos','Wymagane są co najmniej dwa punkty','São necessários pelo menos dois pontos'],
    routeMax: ['Не более 512 точек','Maximal 512 Punkte','512 points au maximum','Máximo de 512 puntos','Maksymalnie 512 punktów','Máximo de 512 pontos'],
    routeSaved: ['Сохранено в моих треках','In Meine Tracks gespeichert','Enregistré dans Mes tracés','Guardado en Mis trazados','Zapisano w Moich śladach','Salvo em Meus traçados'],
    copied: ['Ссылка скопирована','Link kopiert','Lien copié','Enlace copiado','Link skopiowany','Link copiado'],
    error: ['Операция не выполнена','Vorgang fehlgeschlagen','Échec de l’opération','La operación falló','Operacja nie powiodła się','Falha na operação'],
    loading: ['Загрузка…','Laden…','Chargement…','Cargando…','Wczytywanie…','Carregando…']
  };
  const messages = Object.fromEntries(languages.map(language => [language, {}]));
  for (const [key, values] of Object.entries(rows)) {
    languages.forEach((language, index) => {
      const parts = key.split('.');
      let target = messages[language];
      for (const part of parts.slice(0, -1)) target = target[part] ??= {};
      target[parts.at(-1)] = values[index];
    });
  }
  for (const locale of Object.values(messages)) {
    locale.poiTitle = locale.addPoi;
    locale.color = locale.poiColor;
  }
  const notebook = Object.fromEntries(languages.map(language => [
    language, { ...messages[language], routeHelp: messages[language].trackHelp }
  ]));
  window.ServerMapLocales = { messages, notebook };
})();
